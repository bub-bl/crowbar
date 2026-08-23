using System.Numerics;
using System.Runtime.InteropServices;
using Crowbar.Engine.Rendering;
using Crowbar.FileSystems;

namespace Crowbar.Engine;

internal sealed class EnvironmentPreprocessor : IDisposable
{
    private const int EnvironmentSize = 512;
    private const int IrradianceSize = 32;
    private const int PrefilterSize = 128;
    private const int PrefilterMipCount = 8;
    private const uint SampleCount = 256;

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatchUniforms
    {
        public uint Face;
        public uint Size;
        public float Roughness;
        public uint Samples;
    }

    private sealed record CacheKey(
        string Path,
        long Stamp,
        int EnvironmentSize,
        int IrradianceSize,
        int PrefilterSize,
        int PrefilterMipCount,
        uint SampleCount);

    private sealed class CachedResources
    {
        public required ITexture EnvironmentMap { get; init; }
        public required ITexture IrradianceMap { get; init; }
        public required ITexture PrefilteredMap { get; init; }
    }

    private sealed record PendingDecode(CacheKey Key, Task<Texture2D> Task);

    private readonly IGraphicsDevice _device;
    private readonly Dictionary<CacheKey, CachedResources> _cache = [];
    private readonly Dictionary<SceneEnvironment, PendingDecode> _decodes = [];
    private readonly Dictionary<SceneEnvironment, CacheKey> _activeKeys = [];
    private readonly List<IDisposable> _frameResources = [];
    private readonly ISampler _sampler;
    private ITexture? _brdfLut;
    private IComputePipeline? _convertPipeline;
    private IComputePipeline? _irradiancePipeline;
    private IComputePipeline? _prefilterPipeline;
    private IComputePipeline? _brdfPipeline;
    private bool _disposed;

    public EnvironmentPreprocessor(IGraphicsDevice device)
    {
        _device = device;
        _sampler = device.CreateSampler(new SamplerDescription
        {
            AddressMode = SamplerAddressMode.ClampToEdge,
            MipmapFilter = SamplerFilter.Linear
        });
    }

    public void Update(SceneEnvironment? environment, ICommandBuffer commandBuffer)
    {
        if (_disposed || environment is null)
            return;

        if (environment.Sky is ProceduralAtmosphere)
        {
            // Procedural skies are evaluated directly by the sky/IBL shader and
            // have no source texture or compute preprocessing step. Mark the
            // environment ready so Renderer binds it on the next frame.
            if (_decodes.Remove(environment, out var staleDecode))
                ObserveFault(staleDecode.Task);
            _activeKeys.Remove(environment);
            environment.State = EnvironmentPreprocessingState.Ready;
            environment.Diagnostic = null;
            return;
        }

        if (environment.Sky is not CubemapSky sky || string.IsNullOrWhiteSpace(sky.SourcePath))
        {
            if (_decodes.Remove(environment, out var staleDecode))
                ObserveFault(staleDecode.Task);
            _activeKeys.Remove(environment);
            return;
        }
        CacheKey requestedKey;
        try
        {
            requestedKey = new CacheKey(
                sky.SourcePath,
                FileSystem.Content.GetLastWriteTimeUtc(sky.SourcePath).Ticks,
                EnvironmentSize,
                IrradianceSize,
                PrefilterSize,
                PrefilterMipCount,
                SampleCount);
        }
        catch (Exception ex)
        {
            environment.State = EnvironmentPreprocessingState.Failed;
            environment.Diagnostic = ex.Message;
            return;
        }

        if (environment.State == EnvironmentPreprocessingState.Ready &&
            _activeKeys.TryGetValue(environment, out var activeKey) &&
            activeKey == requestedKey)
            return;
        if (environment.State == EnvironmentPreprocessingState.Ready)
            environment.InvalidateRuntime();
        if (environment.State == EnvironmentPreprocessingState.Processing)
            return;

        if (_decodes.TryGetValue(environment, out var pending) && pending.Key != requestedKey)
        {
            _decodes.Remove(environment);
            ObserveFault(pending.Task);
            pending = null;
        }

        if (pending is null)
        {
            environment.State = EnvironmentPreprocessingState.Decoding;
            environment.Diagnostic = null;
            var startedDecode = Task.Run(() =>
            {
                var texture = Texture2D.Load(sky.SourcePath);
                _ = texture.HdrPixels;
                return texture;
            });
            _decodes.Add(environment, new PendingDecode(requestedKey, startedDecode));
            return;
        }

        var decode = pending.Task;
        if (!decode.IsCompleted)
            return;

        _decodes.Remove(environment);
        if (decode.IsFaulted)
        {
            environment.State = EnvironmentPreprocessingState.Failed;
            environment.Diagnostic = decode.Exception?.GetBaseException().Message ?? "HDR decoding failed.";
            return;
        }

        try
        {
            if (!_cache.TryGetValue(requestedKey, out var resources))
            {
                environment.State = EnvironmentPreprocessingState.Processing;
                resources = Process(decode.Result, commandBuffer);
                _cache.Add(requestedKey, resources);
            }

            environment.EnvironmentMap = resources.EnvironmentMap;
            environment.IrradianceMap = resources.IrradianceMap;
            environment.PrefilteredSpecularMap = resources.PrefilteredMap;
            environment.BrdfLut = EnsureBrdfLut(commandBuffer);
            environment.State = EnvironmentPreprocessingState.Ready;
            environment.Diagnostic = null;
            _activeKeys[environment] = requestedKey;
        }
        catch (Exception ex)
        {
            environment.State = EnvironmentPreprocessingState.Unavailable;
            environment.Diagnostic =
                $"GPU environment preprocessing is unavailable: {ex.Message}";
        }
    }

    public void FinishFrame()
    {
        foreach (var resource in _frameResources)
            resource.Dispose();
        _frameResources.Clear();
    }

    private CachedResources Process(Texture2D source, ICommandBuffer commandBuffer)
    {
        var sourceTexture = _device.CreateTexture(new TextureDescription
        {
            Width = source.Width,
            Height = source.Height,
            Format = TextureFormat.Rgba16Float,
            Sampled = true,
            CopyDestination = true
        });
        var sourceBytes = source.GetRgba16FloatBytes();
        unsafe
        {
            fixed (byte* pixels = sourceBytes)
                sourceTexture.Write((nint)pixels, source.Width * 8, 0, 0, source.Width, source.Height);
        }
        _frameResources.Add(sourceTexture);

        var environmentMap = CreateCube(EnvironmentSize, 1);
        DispatchFaces(
            commandBuffer,
            GetPipeline(ref _convertPipeline, "EquirectangularToCube"),
            sourceTexture,
            environmentMap,
            EnvironmentSize,
            0f,
            SampleCount,
            mipLevel: 0);

        var irradianceMap = CreateCube(IrradianceSize, 1);
        DispatchFaces(
            commandBuffer,
            GetPipeline(ref _irradiancePipeline, "Irradiance"),
            environmentMap,
            irradianceMap,
            IrradianceSize,
            0f,
            SampleCount,
            mipLevel: 0);

        var prefilteredMap = CreateCube(PrefilterSize, PrefilterMipCount);
        for (var mip = 0; mip < PrefilterMipCount; mip++)
        {
            var size = Math.Max(1, PrefilterSize >> mip);
            var roughness = mip / (float)(PrefilterMipCount - 1);
            DispatchFaces(
                commandBuffer,
                GetPipeline(ref _prefilterPipeline, "PrefilterSpecular"),
                environmentMap,
                prefilteredMap,
                size,
                roughness,
                SampleCount,
                mip);
        }

        return new CachedResources
        {
            EnvironmentMap = environmentMap,
            IrradianceMap = irradianceMap,
            PrefilteredMap = prefilteredMap
        };
    }

    private ITexture EnsureBrdfLut(ICommandBuffer commandBuffer)
    {
        if (_brdfLut is not null)
            return _brdfLut;

        _brdfLut = _device.CreateTexture(new TextureDescription
        {
            Width = 512,
            Height = 512,
            Format = TextureFormat.Rgba16Float,
            Sampled = true,
            Storage = true
        });
        using var view = _brdfLut.CreateView(new TextureViewDescription
        {
            Dimension = TextureDimension.Dimension2D,
            MipLevelCount = 1,
            ArrayLayerCount = 1
        });
        var pipeline = GetPipeline(ref _brdfPipeline, "BrdfLut");
        using var bindGroup = pipeline.CreateBindGroup(
        [
            new BindGroupBinding { Slot = 0, Texture = view }
        ]);
        using var pass = commandBuffer.BeginComputePass();
        pass.SetPipeline(pipeline);
        pass.SetBindGroup(bindGroup);
        pass.Dispatch(64, 64, 1);
        return _brdfLut;
    }

    private void DispatchFaces(
        ICommandBuffer commandBuffer,
        IComputePipeline pipeline,
        ITexture source,
        ITexture target,
        int size,
        float roughness,
        uint samples,
        int mipLevel)
    {
        for (uint face = 0; face < 6; face++)
        {
            var targetView = target.CreateView(new TextureViewDescription
            {
                Dimension = TextureDimension.Dimension2D,
                BaseMipLevel = mipLevel,
                MipLevelCount = 1,
                BaseArrayLayer = (int)face,
                ArrayLayerCount = 1
            });
            var uniformBuffer = _device.CreateBuffer(new BufferDescription
            {
                Size = (ulong)Marshal.SizeOf<DispatchUniforms>(),
                Usage = BufferUsage.Uniform | BufferUsage.CopyDst
            });
            var uniforms = new DispatchUniforms
            {
                Face = face,
                Size = (uint)size,
                Roughness = roughness,
                Samples = samples
            };
            uniformBuffer.Write(in uniforms);
            var bindGroup = pipeline.CreateBindGroup(
            [
                new BindGroupBinding { Slot = 0, Texture = source },
                new BindGroupBinding { Slot = 1, Sampler = _sampler },
                new BindGroupBinding { Slot = 2, Texture = targetView },
                new BindGroupBinding
                {
                    Slot = 3,
                    Buffer = uniformBuffer,
                    BufferSize = (ulong)Marshal.SizeOf<DispatchUniforms>()
                }
            ]);
            using var pass = commandBuffer.BeginComputePass();
            pass.SetPipeline(pipeline);
            pass.SetBindGroup(bindGroup);
            pass.Dispatch((uint)((size + 7) / 8), (uint)((size + 7) / 8), 1);
            _frameResources.Add(bindGroup);
            _frameResources.Add(uniformBuffer);
            _frameResources.Add(targetView);
        }
    }

    private ITexture CreateCube(int size, int mipLevels) =>
        _device.CreateTexture(new TextureDescription
        {
            Width = size,
            Height = size,
            Dimension = TextureDimension.Cube,
            ArrayLayerCount = 6,
            MipLevelCount = mipLevels,
            Format = TextureFormat.Rgba16Float,
            Sampled = true,
            Storage = true
        });

    private IComputePipeline GetPipeline(ref IComputePipeline? field, string shaderName)
    {
        if (field is not null)
            return field;
        var shader = Shader.Load(PathUtil.Combine("Shaders", $"Environment/{shaderName}.wgsl"));
        field = _device.CreateComputePipeline(new ComputePipelineDescription
        {
            ShaderSource = shader.Source,
            EntryPoint = "cs_main",
            BindGroups = shader.BuildBindGroupLayouts()
        });
        return field;
    }

    private static void ObserveFault(Task task) =>
        _ = task.ContinueWith(
            completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        FinishFrame();
        foreach (var resources in _cache.Values)
        {
            resources.EnvironmentMap.Dispose();
            resources.IrradianceMap.Dispose();
            resources.PrefilteredMap.Dispose();
        }
        _cache.Clear();
        _activeKeys.Clear();
        _brdfLut?.Dispose();
        _convertPipeline?.Dispose();
        _irradiancePipeline?.Dispose();
        _prefilterPipeline?.Dispose();
        _brdfPipeline?.Dispose();
        _sampler.Dispose();
    }
}
