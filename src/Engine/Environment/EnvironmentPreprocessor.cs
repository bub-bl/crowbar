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
    private const int ProceduralSkySize = 256;

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatchUniforms
    {
        public uint Face;
        public uint Size;
        public float Roughness;
        public uint Samples;
    }

    // Mirrors SkyUniforms in Shaders/Environment/ProceduralSky.slang.
    [StructLayout(LayoutKind.Sequential)]
    private struct SkyUniforms
    {
        public uint Face;
        public uint Size;
        public Vector2 Padding;
        public Vector4 Sun;
        public Vector4 Atmosphere;
    }

    /// <summary>Identifies the baked procedural sky: any change regenerates it.</summary>
    private readonly record struct ProceduralKey(Vector4 Sun, Vector4 Atmosphere);

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
    private IComputePipeline? _proceduralSkyPipeline;
    private ProceduralKey? _proceduralActiveKey;
    private CachedResources? _proceduralResources;
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
        if (_disposed)
            return;
        if (environment is null)
        {
            ReleaseProceduralResources();
            return;
        }

        if (environment.Sky is ProceduralAtmosphere)
        {
            // Procedural skies are evaluated analytically by the sky shader,
            // but PBR reflections still need cubemaps: bake the sky into an
            // environment map (then irradiance + prefiltered specular) on the
            // GPU so IBL matches the sky. Regenerated whenever the sun or the
            // atmosphere parameters change.
            if (_decodes.Remove(environment, out var staleDecode))
                ObserveFault(staleDecode.Task);
            _activeKeys.Remove(environment);
            UpdateProcedural(environment, commandBuffer);
            return;
        }

        ReleaseProceduralResources();

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
            var sourcePath = sky.SourcePath;
            var startedDecode = Task.Run(() =>
            {
                var texture = Texture2D.Load(sourcePath);
                if (!texture.IsHdr)
                    throw new InvalidDataException(
                        $"Environment map '{sourcePath}' must be an HDR or EXR image.");
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

    private void UpdateProcedural(SceneEnvironment environment, ICommandBuffer commandBuffer)
    {
        var key = new ProceduralKey(
            new Vector4(
                environment.SunDirection.X,
                environment.SunDirection.Y,
                environment.SunDirection.Z,
                environment.SunAngularRadius * MathF.PI / 180f),
            new Vector4(
                environment.Turbidity,
                environment.GroundAlbedo,
                environment.SunIntensity,
                0f));

        if (_proceduralActiveKey == key && _proceduralResources is not null)
        {
            ApplyProcedural(environment);
            return;
        }

        // The replaced maps may still be sampled this frame through the cached
        // environment bind groups, so they are released at FinishFrame with
        // the other transient resources instead of being disposed here.
        ReleaseProceduralResources();

        try
        {
            _proceduralResources = GenerateProcedural(environment, commandBuffer);
            _proceduralActiveKey = key;
            ApplyProcedural(environment);
        }
        catch (Exception ex)
        {
            environment.State = EnvironmentPreprocessingState.Unavailable;
            environment.Diagnostic = $"GPU procedural sky generation is unavailable: {ex.Message}";
            _proceduralActiveKey = null;
        }
    }

    private static void ApplyProcedural(SceneEnvironment environment)
    {
        environment.State = EnvironmentPreprocessingState.Ready;
        environment.Diagnostic = null;
    }

    private CachedResources GenerateProcedural(SceneEnvironment environment, ICommandBuffer commandBuffer)
    {
        var skyCube = CreateCube(ProceduralSkySize, 1);
        DispatchProceduralSky(commandBuffer, skyCube, ProceduralSkySize, environment);

        var irradianceMap = CreateCube(IrradianceSize, 1);
        DispatchFaces(
            commandBuffer,
            GetPipeline(ref _irradiancePipeline, "Irradiance"),
            skyCube,
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
                skyCube,
                prefilteredMap,
                size,
                roughness,
                SampleCount,
                mip);
        }

        environment.EnvironmentMap = skyCube;
        environment.IrradianceMap = irradianceMap;
        environment.PrefilteredSpecularMap = prefilteredMap;
        environment.BrdfLut = EnsureBrdfLut(commandBuffer);
        return new CachedResources
        {
            EnvironmentMap = skyCube,
            IrradianceMap = irradianceMap,
            PrefilteredMap = prefilteredMap
        };
    }

    private void DispatchProceduralSky(
        ICommandBuffer commandBuffer,
        ITexture target,
        int size,
        SceneEnvironment environment)
    {
        var pipeline = GetPipeline(ref _proceduralSkyPipeline, "ProceduralSky");
        var sun = new Vector4(
            environment.SunDirection.X,
            environment.SunDirection.Y,
            environment.SunDirection.Z,
            environment.SunAngularRadius * MathF.PI / 180f);
        var atmosphere = new Vector4(
            environment.Turbidity,
            environment.GroundAlbedo,
            environment.SunIntensity,
            0f);

        for (uint face = 0; face < 6; face++)
        {
            var targetView = target.CreateView(new TextureViewDescription
            {
                Dimension = TextureDimension.Dimension2D,
                MipLevelCount = 1,
                BaseArrayLayer = (int)face,
                ArrayLayerCount = 1
            });
            var uniformBuffer = _device.CreateBuffer(new BufferDescription
            {
                Size = (ulong)Marshal.SizeOf<SkyUniforms>(),
                Usage = BufferUsage.Uniform | BufferUsage.CopyDst
            });
            var uniforms = new SkyUniforms
            {
                Face = face,
                Size = (uint)size,
                Sun = sun,
                Atmosphere = atmosphere
            };
            uniformBuffer.Write(in uniforms);
            var bindGroup = pipeline.CreateBindGroup(
            [
                new BindGroupBinding { Slot = 0, Texture = targetView },
                new BindGroupBinding
                {
                    Slot = 1,
                    Buffer = uniformBuffer,
                    BufferSize = (ulong)Marshal.SizeOf<SkyUniforms>()
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

    private void ReleaseProceduralResources()
    {
        if (_proceduralResources is null)
            return;
        // Moved to the frame resource list: released at FinishFrame so bind
        // groups recorded earlier this frame keep valid textures.
        _frameResources.Add(_proceduralResources.EnvironmentMap);
        _frameResources.Add(_proceduralResources.IrradianceMap);
        _frameResources.Add(_proceduralResources.PrefilteredMap);
        _proceduralResources = null;
        _proceduralActiveKey = null;
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
        if (_proceduralResources is not null)
        {
            _proceduralResources.EnvironmentMap.Dispose();
            _proceduralResources.IrradianceMap.Dispose();
            _proceduralResources.PrefilteredMap.Dispose();
            _proceduralResources = null;
        }
        _proceduralActiveKey = null;
        _brdfLut?.Dispose();
        _convertPipeline?.Dispose();
        _irradiancePipeline?.Dispose();
        _prefilterPipeline?.Dispose();
        _brdfPipeline?.Dispose();
        _proceduralSkyPipeline?.Dispose();
        _sampler.Dispose();
    }
}
