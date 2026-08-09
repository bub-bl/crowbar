using Crowbar.Engine.Rendering;
using Silk.NET.WebGPU;
using System.Runtime.InteropServices;
using System.Numerics;
using Crowbar.UI;

namespace Crowbar.Engine;

/// <summary>
/// Owns the first usable WebGPU device for the runtime.
/// Owns the window surface and keeps its configuration synchronized with the framebuffer size.
/// </summary>
public sealed unsafe class WebGpuContext : IDisposable
{
    private struct CameraUniforms
    {
        public Matrix4x4 View;
        public Matrix4x4 Projection;
    }

    public WebGpuRuntime Runtime { get; }
    public WebGpuAdapter Adapter { get; }
    public WebGpuDevice Device { get; }
    public WebGpuQueue Queue { get; }

    private Surface* _surface;
    private ShaderModule* _cubeShader;
    private RenderPipeline* _cubePipeline;
    private Silk.NET.WebGPU.Buffer* _cubeVertexBuffer;
    private Silk.NET.WebGPU.Buffer* _cameraUniformBuffer;
    private BindGroupLayout* _cameraBindGroupLayout;
    private BindGroup* _cameraBindGroup;
    private PipelineLayout* _cameraPipelineLayout;
    private Texture* _depthTexture;
    private TextureView* _depthTextureView;
    private const uint CubeVertexCount = 36;
    private TextureFormat _surfaceFormat;
    private readonly nint _windowHandle;
    private int _width;
    private int _height;
    private Vector3 _cameraPosition = new(4.24f, 3f, 4.24f);
    private float _cameraYaw = -MathF.PI / 4f;
    private float _cameraPitch = -0.42f;
    private bool _mouseLookActive;
    private int _mouseCenterX;
    private int _mouseCenterY;
    private bool _hasPresentedFrame;
    private bool _disposed;
    public UiSystem? Ui { get; set; }
    private Texture* _uiTexture;
    private TextureView* _uiTextureView;
    private Sampler* _uiSampler;
    private ShaderModule* _uiShader;
    private RenderPipeline* _uiPipeline;
    private Silk.NET.WebGPU.Buffer* _uiVertexBuffer;
    private BindGroupLayout* _uiBindGroupLayout;
    private BindGroup* _uiBindGroup;
    private int _uiWidth;
    private int _uiHeight;
    private bool _uiTextureDirty = true;

    // Offscreen 3D scene: the cube renders here instead of directly on the
    // surface, then the scene is blitted to the surface. backdrop-filter
    // panels are composited on the GPU by Backdrop.wgsl sampling this texture
    // directly (like S&box's ui_backdropfilter.shader), so the CPU never sees
    // the scene and the Skia UI raster only re-runs when the UI changes.
    private Texture* _sceneTexture;
    private TextureView* _sceneTextureView;
    private BindGroup* _sceneBindGroup;

    // Backdrop compositor: one instanced fullscreen quad per backdrop-filter
    // region, parameters in a read-only storage buffer (16 regions max).
    private const uint MaxBackdropRegions = 16;
    private ShaderModule* _backdropShader;
    private RenderPipeline* _backdropPipeline;
    private PipelineLayout* _backdropPipelineLayout;
    private BindGroupLayout* _backdropBindGroupLayout;
    private BindGroup* _backdropBindGroup;
    private Silk.NET.WebGPU.Buffer* _backdropParamsBuffer;
    private byte[]? _backdropParamsBytes;

    [StructLayout(LayoutKind.Sequential)]
    private struct BackdropGpuParams
    {
        public Vector4 Region;     // xy = border-box top-left (px), zw = size (px)
        public Vector4 UvRect;     // normalized scene-texture coords (u0, v0, u1, v1)
        public Vector4 RadiusBlur; // x = corner radius, y = blur sigma, z = panel alpha
        public Vector4 Tint;       // panel background color (straight alpha)
        public Vector4 Op0, Op1, Op2, Op3, Op4, Op5, Op6, Op7;
        public Vector4 OpCount;    // x = active op count
    }

    public WebGpuContext(nint windowHandle, int width, int height)
    {
        Runtime = new WebGpuRuntime();
        try
        {
            if (windowHandle == 0)
                throw new ArgumentException("The window does not expose a native handle.", nameof(windowHandle));
            _windowHandle = windowHandle;
            _width = Math.Max(1, width);
            _height = Math.Max(1, height);

            var hwndDescriptor = new SurfaceDescriptorFromWindowsHWND
            {
                Chain = new ChainedStruct { SType = SType.SurfaceDescriptorFromWindowsHwnd },
                Hwnd = (void*)windowHandle,
                Hinstance = (void*)System.Runtime.InteropServices.Marshal.GetHINSTANCE(
                    typeof(WebGpuContext).Module)
            };
            var surfaceDescriptor = new SurfaceDescriptor
            {
                NextInChain = (ChainedStruct*)&hwndDescriptor
            };
            _surface = Runtime.Api.InstanceCreateSurface(Runtime.Instance.UnsafeHandle, in surfaceDescriptor);
            if (_surface == null)
                throw new InvalidOperationException("WebGPU could not create a window surface.");

            Adapter = new WebGpuAdapter(Runtime, WebGpuSurface.FromNative((nint)_surface));
            Device = Adapter.CreateDevice();
            Queue = Device.GetQueue();
            Runtime.ConfigureDebugCallback(Device);
            _surfaceFormat = Runtime.Api.SurfaceGetPreferredFormat(_surface, Adapter.UnsafeHandle);
            if (_surfaceFormat == TextureFormat.Undefined)
                _surfaceFormat = TextureFormat.Bgra8Unorm;

            ConfigureSurface(width, height);
            CreateCameraResources();
            CreateCubeResources();
            CreateUiResources(_width, _height);
            CreateBackdropResources();
            CreateSceneResources(_width, _height);
            UpdateCamera(0);
            Console.WriteLine("WebGPU device initialized.");
        }
        catch
        {
            Runtime.Dispose();
            throw;
        }
    }

    public void Render(double _)
    {
        if (_disposed || _surface == null)
            return;

        SurfaceTexture surfaceTexture = default;
        Runtime.Api.SurfaceGetCurrentTexture(_surface, ref surfaceTexture);
        if (surfaceTexture.Texture == null)
            return;

        TextureView* view = Runtime.Api.TextureCreateView(surfaceTexture.Texture, null);
        if (view == null)
            return;

        WebGpuCommandEncoder encoder = Runtime.CreateCommandEncoder(Device);

        // Pass 1: render the 3D scene into the offscreen scene texture (it is
        // both blitted to the surface and copied back for the UI backdrop).
        var scenePassDescription = new RenderPassDescription
        {
            Color = new ColorAttachment
            {
                View = WebGpuTextureView.FromNative((nint)_sceneTextureView),
                LoadOp = RenderAttachmentLoadOp.Clear,
                StoreOp = RenderAttachmentStoreOp.Store,
                ClearColor = new System.Numerics.Vector4(0.06f, 0.09f, 0.16f, 1f)
            },
            Depth = new DepthAttachment
            {
                View = WebGpuTextureView.FromNative((nint)_depthTextureView),
                LoadOp = RenderAttachmentLoadOp.Clear,
                StoreOp = RenderAttachmentStoreOp.Store,
                ClearValue = 1f
            }
        };
        WebGpuRenderPassEncoder scenePass = Runtime.BeginRenderPass(encoder, scenePassDescription);
        Runtime.SetPipeline(scenePass, WebGpuRenderPipeline.FromNative((nint)_cubePipeline));
        Runtime.SetBindGroup(scenePass, WebGpuBindGroup.FromNative((nint)_cameraBindGroup), 0);
        Runtime.SetVertexBuffer(scenePass, WebGpuBuffer.FromNative((nint)_cubeVertexBuffer),
            (ulong)(CubeVertexCount * 6 * sizeof(float)));
        Runtime.Draw(scenePass, CubeVertexCount);
        Runtime.EndRenderPass(scenePass);

        // Pass 2: composite the scene, the backdrop-filter regions and the UI
        // onto the surface. The scene blit reuses the UI pipeline (opaque
        // texture, so the alpha blend is a plain overwrite); the backdrop
        // compositor samples the scene texture on the GPU between the blit and
        // the UI overlay.
        var surfacePassDescription = new RenderPassDescription
        {
            Color = new ColorAttachment
            {
                View = WebGpuTextureView.FromNative((nint)view),
                LoadOp = RenderAttachmentLoadOp.Clear,
                StoreOp = RenderAttachmentStoreOp.Store,
                ClearColor = new System.Numerics.Vector4(0.06f, 0.09f, 0.16f, 1f)
            },
            Depth = new DepthAttachment
            {
                View = WebGpuTextureView.FromNative((nint)_depthTextureView),
                LoadOp = RenderAttachmentLoadOp.Clear,
                StoreOp = RenderAttachmentStoreOp.Store,
                ClearValue = 1f
            }
        };
        WebGpuRenderPassEncoder surfacePass = Runtime.BeginRenderPass(encoder, surfacePassDescription);
        Runtime.SetPipeline(surfacePass, WebGpuRenderPipeline.FromNative((nint)_uiPipeline));
        Runtime.SetBindGroup(surfacePass, WebGpuBindGroup.FromNative((nint)_sceneBindGroup), 0);
        Runtime.SetVertexBuffer(surfacePass, WebGpuBuffer.FromNative((nint)_uiVertexBuffer), (ulong)(6 * 4 * sizeof(float)));
        Runtime.Draw(surfacePass, 6);

        // backdrop-filter: one instanced fullscreen quad per region, sampling
        // the 3D scene texture with blur + color transforms. The params come
        // from the Skia raster pass, which only records regions it could not
        // bake on the CPU itself, so moving the camera never re-rasterizes the
        // UI. The UI overlay is drawn on top afterwards (regions are painted
        // between the scene and the UI, like S&box's ui_backdropfilter).
        var backdrops = Ui?.Renderer.Backdrops;
        if (backdrops is { Count: > 0 } && _backdropPipeline != null && _backdropBindGroup != null)
        {
            UpdateBackdropParams(backdrops);
            Runtime.SetPipeline(surfacePass, WebGpuRenderPipeline.FromNative((nint)_backdropPipeline));
            Runtime.SetBindGroup(surfacePass, WebGpuBindGroup.FromNative((nint)_backdropBindGroup), 0);
            Runtime.SetVertexBuffer(surfacePass, WebGpuBuffer.FromNative((nint)_uiVertexBuffer), (ulong)(6 * 4 * sizeof(float)));
            Runtime.DrawInstanced(surfacePass, 6, (uint)Math.Min(backdrops.Count, MaxBackdropRegions));
        }

        if (Ui is not null && _uiPipeline != null && _uiBindGroup != null)
        {
            // Ui.Render() peut découvrir une invalidation de layout/style (par
            // exemple :hover) et marquer le renderer dirty juste avant de
            // rasteriser. Il faut donc interroger l'état de l'UI avant Render,
            // pas uniquement Renderer.IsDirty à cet instant.
            bool uiChanged = _uiTextureDirty || Ui.IsDirty;
            var pixels = Ui.Render();
            if (uiChanged && pixels.Length > 0)
            {
                UpdateUiTexture(pixels.Span);
                _uiTextureDirty = false;
            }
            Runtime.SetPipeline(surfacePass, WebGpuRenderPipeline.FromNative((nint)_uiPipeline));
            Runtime.SetBindGroup(surfacePass, WebGpuBindGroup.FromNative((nint)_uiBindGroup), 0);
            Runtime.SetVertexBuffer(surfacePass, WebGpuBuffer.FromNative((nint)_uiVertexBuffer), (ulong)(6 * 4 * sizeof(float)));
            Runtime.Draw(surfacePass, 6);
        }
        Runtime.EndRenderPass(surfacePass);

        WebGpuCommandBuffer commandBuffer = Runtime.FinishCommandEncoder(encoder);
        Runtime.Submit(Queue, commandBuffer);
        Runtime.ReleaseCommandBuffer(commandBuffer);
        Runtime.ReleaseCommandEncoder(encoder);
        Runtime.Api.TextureViewRelease(view);
        Runtime.Api.SurfacePresent(_surface);

        if (!_hasPresentedFrame)
        {
            _hasPresentedFrame = true;
            Console.WriteLine("WebGPU first frame presented.");
        }
    }

    public void Update(double deltaTime)
    {
        if (_disposed)
            return;

        // GetAsyncKeyState is process-independent, so explicitly reject input
        // while another window is in the foreground.
        if (!IsWindowFocused())
        {
            _mouseLookActive = false;
            return;
        }

        float delta = Math.Clamp((float)deltaTime, 0f, 0.1f);
        UpdateMouseLook();

        Vector3 forward = new(
            MathF.Sin(_cameraYaw) * MathF.Cos(_cameraPitch),
            MathF.Sin(_cameraPitch),
            -MathF.Cos(_cameraYaw) * MathF.Cos(_cameraPitch));
        Vector3 right = new(MathF.Cos(_cameraYaw), 0f, MathF.Sin(_cameraYaw));
        Vector3 movement = Vector3.Zero;
        if (IsKeyDown(0x5A)) movement += forward; // Z
        if (IsKeyDown(0x53)) movement -= forward; // S
        if (IsKeyDown(0x44)) movement += right;   // D
        if (IsKeyDown(0x51)) movement -= right;   // Q
        if (IsKeyDown(0x20)) movement += Vector3.UnitY; // Espace
        if (IsKeyDown(0x45)) movement -= Vector3.UnitY; // E

        if (movement.LengthSquared() > 0f)
            _cameraPosition += Vector3.Normalize(movement) * (2.5f * delta);

        UpdateCamera(delta);
    }

    public void Resize(int width, int height)
    {
        if (_disposed || _surface == null || width <= 0 || height <= 0)
            return;

        _width = width;
        _height = height;
        ConfigureSurface(width, height);
        UpdateCamera(0);
        CreateUiResources(width, height);
        CreateSceneResources(width, height);
    }

    /// <summary>
    /// Creates the offscreen scene texture and the two bind groups that sample
    /// it: the blit (scene onto the surface, reusing the UI pipeline layout)
    /// and the backdrop compositor (scene + sampler + per-region params).
    /// </summary>
    private void CreateSceneResources(int width, int height)
    {
        if (_backdropBindGroup != null) { Runtime.Api.BindGroupRelease(_backdropBindGroup); _backdropBindGroup = null; }
        if (_sceneBindGroup != null) { Runtime.Api.BindGroupRelease(_sceneBindGroup); _sceneBindGroup = null; }
        if (_sceneTextureView != null) { Runtime.Api.TextureViewRelease(_sceneTextureView); _sceneTextureView = null; }
        if (_sceneTexture != null) { Runtime.Api.TextureDestroy(_sceneTexture); Runtime.Api.TextureRelease(_sceneTexture); _sceneTexture = null; }

        width = Math.Max(1, width);
        height = Math.Max(1, height);

        var textureDescriptor = new TextureDescriptor
        {
            Usage = TextureUsage.RenderAttachment | TextureUsage.TextureBinding,
            Dimension = TextureDimension.Dimension2D,
            Size = new Extent3D { Width = (uint)width, Height = (uint)height, DepthOrArrayLayers = 1 },
            Format = _surfaceFormat,
            MipLevelCount = 1,
            SampleCount = 1
        };
        _sceneTexture = Runtime.Api.DeviceCreateTexture(Device.UnsafeHandle, in textureDescriptor);
        _sceneTextureView = Runtime.Api.TextureCreateView(_sceneTexture, null);

        var bindEntries = stackalloc BindGroupEntry[2];
        bindEntries[0] = new BindGroupEntry { Binding = 0, TextureView = _sceneTextureView };
        bindEntries[1] = new BindGroupEntry { Binding = 1, Sampler = _uiSampler };
        var bindDescriptor = new BindGroupDescriptor
        {
            Layout = _uiBindGroupLayout,
            EntryCount = 2,
            Entries = bindEntries
        };
        _sceneBindGroup = Runtime.Api.DeviceCreateBindGroup(Device.UnsafeHandle, in bindDescriptor);

        if (_backdropBindGroupLayout != null && _backdropParamsBuffer != null)
        {
            var backdropEntries = stackalloc BindGroupEntry[3];
            backdropEntries[0] = new BindGroupEntry { Binding = 0, TextureView = _sceneTextureView };
            backdropEntries[1] = new BindGroupEntry { Binding = 1, Sampler = _uiSampler };
            backdropEntries[2] = new BindGroupEntry
            {
                Binding = 2,
                Buffer = _backdropParamsBuffer,
                Size = (ulong)(MaxBackdropRegions * sizeof(BackdropGpuParams))
            };
            var backdropDescriptor = new BindGroupDescriptor
            {
                Layout = _backdropBindGroupLayout,
                EntryCount = 3,
                Entries = backdropEntries
            };
            _backdropBindGroup = Runtime.Api.DeviceCreateBindGroup(Device.UnsafeHandle, in backdropDescriptor);
        }
    }

    /// <summary>
    /// Creates the backdrop compositor: the Backdrop.wgsl pipeline (instanced
    /// fullscreen quads, one per backdrop-filter region) and the storage buffer
    /// holding the per-region parameters. The bind group is created in
    /// <see cref="CreateSceneResources"/> because it references the scene
    /// texture view, which is recreated on resize.
    /// </summary>
    private void CreateBackdropResources()
    {
        if (_backdropPipelineLayout != null) Runtime.Api.PipelineLayoutRelease(_backdropPipelineLayout);
        if (_backdropBindGroupLayout != null) Runtime.Api.BindGroupLayoutRelease(_backdropBindGroupLayout);
        if (_backdropPipeline != null) Runtime.Api.RenderPipelineRelease(_backdropPipeline);
        if (_backdropShader != null) Runtime.Api.ShaderModuleRelease(_backdropShader);
        if (_backdropParamsBuffer != null)
        {
            Runtime.Api.BufferDestroy(_backdropParamsBuffer);
            Runtime.Api.BufferRelease(_backdropParamsBuffer);
        }

        var paramsDescriptor = new BufferDescriptor
        {
            Size = (ulong)(MaxBackdropRegions * sizeof(BackdropGpuParams)),
            Usage = BufferUsage.Storage | BufferUsage.CopyDst,
            MappedAtCreation = false
        };
        _backdropParamsBuffer = Runtime.Api.DeviceCreateBuffer(Device.UnsafeHandle, in paramsDescriptor);
        _backdropParamsBytes = null;

        BindGroupLayoutEntry* entries = stackalloc BindGroupLayoutEntry[3];
        entries[0] = new BindGroupLayoutEntry
        {
            Binding = 0,
            Visibility = ShaderStage.Fragment,
            Texture = new TextureBindingLayout { SampleType = TextureSampleType.Float, ViewDimension = TextureViewDimension.Dimension2D }
        };
        entries[1] = new BindGroupLayoutEntry
        {
            Binding = 1,
            Visibility = ShaderStage.Fragment,
            Sampler = new SamplerBindingLayout { Type = SamplerBindingType.Filtering }
        };
        entries[2] = new BindGroupLayoutEntry
        {
            Binding = 2,
            Visibility = ShaderStage.Fragment,
            Buffer = new BufferBindingLayout { Type = BufferBindingType.ReadOnlyStorage }
        };
        var layoutDescriptor = new BindGroupLayoutDescriptor { EntryCount = 3, Entries = entries };
        _backdropBindGroupLayout = Runtime.Api.DeviceCreateBindGroupLayout(Device.UnsafeHandle, in layoutDescriptor);
        BindGroupLayout* layout = _backdropBindGroupLayout;
        var pipelineLayoutDescriptor = new PipelineLayoutDescriptor { BindGroupLayoutCount = 1, BindGroupLayouts = &layout };
        _backdropPipelineLayout = Runtime.Api.DeviceCreatePipelineLayout(Device.UnsafeHandle, in pipelineLayoutDescriptor);

        string shaderSource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "Backdrop.wgsl"));
        nint code = ToUtf8HGlobal(shaderSource), vertexEntry = ToUtf8HGlobal("vs_main"), fragmentEntry = ToUtf8HGlobal("fs_main");
        try
        {
            var wgsl = new ShaderModuleWGSLDescriptor { Code = (byte*)code };
            wgsl.Chain.SType = SType.ShaderModuleWgslDescriptor;
            var shaderDescriptor = new ShaderModuleDescriptor { NextInChain = (ChainedStruct*)&wgsl };
            _backdropShader = Runtime.Api.DeviceCreateShaderModule(Device.UnsafeHandle, in shaderDescriptor);
            // src-over: inside the border box the quad is opaque and replaces the
            // scene; outside it the mask is zero so the surface is untouched.
            var blend = new BlendState
            {
                Color = new BlendComponent { Operation = BlendOperation.Add, SrcFactor = BlendFactor.SrcAlpha, DstFactor = BlendFactor.OneMinusSrcAlpha },
                Alpha = new BlendComponent { Operation = BlendOperation.Add, SrcFactor = BlendFactor.One, DstFactor = BlendFactor.OneMinusSrcAlpha }
            };
            var target = new ColorTargetState { Format = _surfaceFormat, WriteMask = ColorWriteMask.All, Blend = &blend };
            var fragment = new FragmentState { Module = _backdropShader, EntryPoint = (byte*)fragmentEntry, TargetCount = 1, Targets = &target };
            VertexAttribute* attrs = stackalloc VertexAttribute[2];
            attrs[0] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 0, ShaderLocation = 0 };
            attrs[1] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 2 * sizeof(float), ShaderLocation = 1 };
            var vb = new VertexBufferLayout { ArrayStride = 4 * sizeof(float), StepMode = VertexStepMode.Vertex, AttributeCount = 2, Attributes = attrs };
            var vertex = new VertexState { Module = _backdropShader, EntryPoint = (byte*)vertexEntry, BufferCount = 1, Buffers = &vb };
            var depthStencil = new DepthStencilState { Format = TextureFormat.Depth24Plus, DepthWriteEnabled = false, DepthCompare = CompareFunction.Always, StencilFront = new StencilFaceState { Compare = CompareFunction.Always }, StencilBack = new StencilFaceState { Compare = CompareFunction.Always } };
            var pipelineDescriptor = new RenderPipelineDescriptor
            {
                Layout = _backdropPipelineLayout,
                Vertex = vertex,
                Primitive = new PrimitiveState { Topology = PrimitiveTopology.TriangleList, FrontFace = FrontFace.Ccw, CullMode = CullMode.None },
                DepthStencil = &depthStencil,
                Multisample = new MultisampleState { Count = 1, Mask = 0xFFFFFFFF },
                Fragment = &fragment
            };
            _backdropPipeline = Runtime.Api.DeviceCreateRenderPipeline(Device.UnsafeHandle, in pipelineDescriptor);
        }
        finally
        {
            Marshal.FreeHGlobal(code);
            Marshal.FreeHGlobal(vertexEntry);
            Marshal.FreeHGlobal(fragmentEntry);
        }
    }

    /// <summary>
    /// Translates the backdrop regions collected by the Skia pass into the GPU
    /// parameter buffer (Backdrop.wgsl reads one struct per instance) and
    /// uploads it only when the set actually changed, so a static UI costs no
    /// uploads at all. The regions already carry the CSS chain in a form the
    /// shader can evaluate (see
    /// <see cref="Crowbar.UI.CssFilterFunctions.IsGpuBackdropExpressible"/>).
    /// </summary>
    private void UpdateBackdropParams(IReadOnlyList<BackdropRegion> regions)
    {
        if (_backdropParamsBuffer == null) return;
        var count = Math.Min(regions.Count, (int)MaxBackdropRegions);
        var bytes = new byte[count * sizeof(BackdropGpuParams)];
        for (var i = 0; i < count; i++)
        {
            var region = regions[i];
            var p = new BackdropGpuParams
            {
                Region = new Vector4(region.X, region.Y, region.Width, region.Height),
                UvRect = new Vector4(
                    region.X / _width, region.Y / _height,
                    (region.X + region.Width) / _width, (region.Y + region.Height) / _height),
                RadiusBlur = new Vector4(region.Radius, 0, region.Alpha, 0),
                Tint = new Vector4(
                    region.Tint.R / 255f, region.Tint.G / 255f,
                    region.Tint.B / 255f, region.Tint.A / 255f),
                OpCount = new Vector4(0, 0, 0, 0)
            };
            var opIndex = 0;
            foreach (var function in region.Filter.Functions)
            {
                var name = function.Name;
                if (name.Equals("blur", StringComparison.OrdinalIgnoreCase))
                {
                    p.RadiusBlur.Y = function.Parameters[0];
                }
                else if (opIndex < 8)
                {
                    var amount = function.Parameters[0];
                    float type = name switch
                    {
                        "brightness" => 0,
                        "contrast" => 1,
                        "saturate" => 2,
                        "grayscale" => 2, // grayscale(a) == saturate(1-a)
                        "invert" => 3,
                        "hue-rotate" => 4,
                        "sepia" => 5,
                        "opacity" => 6,
                        _ => -1
                    };
                    if (type < 0) continue;
                    if (name.Equals("grayscale", StringComparison.OrdinalIgnoreCase)) amount = 1 - amount;
                    SetBackdropOp(ref p, opIndex++, type, amount);
                }
            }
            p.OpCount.X = opIndex;
            var offset = i * sizeof(BackdropGpuParams);
            unsafe
            {
                fixed (byte* data = bytes)
                    *(BackdropGpuParams*)(data + offset) = p;
            }
        }
        if (_backdropParamsBytes is not null && _backdropParamsBytes.AsSpan().SequenceEqual(bytes))
            return;
        _backdropParamsBytes = bytes;
        fixed (byte* data = bytes)
        {
            Runtime.Api.QueueWriteBuffer((Queue*)Queue.NativeHandle, _backdropParamsBuffer, 0, data,
                (nuint)bytes.Length);
        }
    }

    private static void SetBackdropOp(ref BackdropGpuParams p, int index, float type, float amount)
    {
        var op = new Vector4(type, amount, 0, 0);
        switch (index)
        {
            case 0: p.Op0 = op; break;
            case 1: p.Op1 = op; break;
            case 2: p.Op2 = op; break;
            case 3: p.Op3 = op; break;
            case 4: p.Op4 = op; break;
            case 5: p.Op5 = op; break;
            case 6: p.Op6 = op; break;
            case 7: p.Op7 = op; break;
        }
    }

    /// <summary>
    /// Copies a string into a null-terminated UTF-8 buffer allocated with
    /// Marshal.AllocHGlobal (freed by Marshal.FreeHGlobal). wgpu reads shader
    /// sources as UTF-8, so passing the ANSI conversion would mangle any
    /// non-ASCII byte and make wgpu reject the module.
    /// </summary>
    private static nint ToUtf8HGlobal(string text)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        nint pointer = Marshal.AllocHGlobal(bytes.Length + 1);
        Marshal.Copy(bytes, 0, pointer, bytes.Length);
        Marshal.WriteByte(pointer, bytes.Length, 0);
        return pointer;
    }

    private void ConfigureSurface(int width, int height)
    {
        var configuration = new SurfaceConfiguration
        {
            Device = Device.UnsafeHandle,
            Width = (uint)Math.Max(1, width),
            Height = (uint)Math.Max(1, height),
            Format = _surfaceFormat,
            Usage = TextureUsage.RenderAttachment,
            PresentMode = PresentMode.Fifo,
            AlphaMode = CompositeAlphaMode.Auto
        };
        Runtime.Api.SurfaceConfigure(_surface, in configuration);
        RecreateDepthTexture(width, height);
    }

    private void RecreateDepthTexture(int width, int height)
    {
        if (_depthTextureView != null)
            Runtime.Api.TextureViewRelease(_depthTextureView);
        if (_depthTexture != null)
        {
            Runtime.Api.TextureDestroy(_depthTexture);
            Runtime.Api.TextureRelease(_depthTexture);
        }

        var descriptor = new TextureDescriptor
        {
            Usage = TextureUsage.RenderAttachment,
            Dimension = TextureDimension.Dimension2D,
            Size = new Extent3D
            {
                Width = (uint)Math.Max(1, width),
                Height = (uint)Math.Max(1, height),
                DepthOrArrayLayers = 1
            },
            Format = TextureFormat.Depth24Plus,
            MipLevelCount = 1,
            SampleCount = 1
        };
        _depthTexture = Runtime.Api.DeviceCreateTexture(Device.UnsafeHandle, in descriptor);
        if (_depthTexture == null)
            throw new InvalidOperationException("WebGPU could not create the depth texture.");

        _depthTextureView = Runtime.Api.TextureCreateView(_depthTexture, null);
        if (_depthTextureView == null)
            throw new InvalidOperationException("WebGPU could not create the depth texture view.");
    }

    private void UpdateCamera(double _)
    {
        float aspect = Math.Max(1, _width) / (float)Math.Max(1, _height);
        Vector3 target = _cameraPosition + new Vector3(
            MathF.Sin(_cameraYaw) * MathF.Cos(_cameraPitch),
            MathF.Sin(_cameraPitch),
            -MathF.Cos(_cameraYaw) * MathF.Cos(_cameraPitch));
        Matrix4x4 view = Matrix4x4.CreateLookAt(_cameraPosition, target, Vector3.UnitY);
        Matrix4x4 projection = Matrix4x4.CreatePerspectiveFieldOfView(
            MathF.PI / 3f, aspect, 0.1f, 100f);
        CameraUniforms uniforms = new()
        {
            View = view,
            Projection = projection
        };
        Runtime.Api.QueueWriteBuffer(
            (Queue*)Queue.NativeHandle,
            _cameraUniformBuffer,
            0,
            in uniforms,
            (nuint)sizeof(CameraUniforms));
    }

    private void UpdateMouseLook()
    {
        if (!IsKeyDown(0x02)) // VK_RBUTTON
        {
            _mouseLookActive = false;
            return;
        }

        if (!_mouseLookActive)
        {
            CenterCursor();
            _mouseLookActive = true;
            return;
        }

        if (!GetCursorPos(out Point cursor))
            return;

        float deltaX = cursor.X - _mouseCenterX;
        float deltaY = cursor.Y - _mouseCenterY;
        CenterCursor();
        _cameraYaw += deltaX * 0.003f;
        _cameraPitch = Math.Clamp(_cameraPitch - deltaY * 0.003f, -1.45f, 1.45f);
    }

    private void CenterCursor()
    {
        if (!GetClientRect(_windowHandle, out Rect client))
            return;

        Point center = new((client.Left + client.Right) / 2, (client.Top + client.Bottom) / 2);
        if (!ClientToScreen(_windowHandle, ref center))
            return;

        _mouseCenterX = center.X;
        _mouseCenterY = center.Y;
        SetCursorPos(_mouseCenterX, _mouseCenterY);
    }

    private static bool IsKeyDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    private bool IsWindowFocused() => GetForegroundWindow() == _windowHandle;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(nint window, ref Point point);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; public Point(int x, int y) => (X, Y) = (x, y); }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    private void CreateCameraResources()
    {
        var bufferDescriptor = new BufferDescriptor
        {
            Size = (ulong)sizeof(CameraUniforms),
            Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
            MappedAtCreation = false
        };
        _cameraUniformBuffer = Runtime.Api.DeviceCreateBuffer(Device.UnsafeHandle, in bufferDescriptor);

        var layoutEntry = new BindGroupLayoutEntry
        {
            Binding = 0,
            Visibility = ShaderStage.Vertex,
            Buffer = new BufferBindingLayout { Type = BufferBindingType.Uniform }
        };
        var layoutDescriptor = new BindGroupLayoutDescriptor
        {
            EntryCount = 1,
            Entries = &layoutEntry
        };
        _cameraBindGroupLayout = Runtime.Api.DeviceCreateBindGroupLayout(Device.UnsafeHandle, in layoutDescriptor);

        BindGroupLayout* cameraLayout = _cameraBindGroupLayout;
        var pipelineLayoutDescriptor = new PipelineLayoutDescriptor
        {
            BindGroupLayoutCount = 1,
            BindGroupLayouts = &cameraLayout
        };
        _cameraPipelineLayout = Runtime.Api.DeviceCreatePipelineLayout(
            Device.UnsafeHandle, in pipelineLayoutDescriptor);

        var bindGroupEntry = new BindGroupEntry
        {
            Binding = 0,
            Buffer = _cameraUniformBuffer,
            Size = (ulong)sizeof(CameraUniforms)
        };
        var bindGroupDescriptor = new BindGroupDescriptor
        {
            Layout = _cameraBindGroupLayout,
            EntryCount = 1,
            Entries = &bindGroupEntry
        };
        _cameraBindGroup = Runtime.Api.DeviceCreateBindGroup(Device.UnsafeHandle, in bindGroupDescriptor);
    }

    private void CreateCubeResources()
    {
        string shaderSource = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Shaders", "Cube.wgsl"));

        float[] vertices =
        [
            // Back
            -0.5f, -0.5f, -0.35f, 0.8f, 0.2f, 0.2f,  0.5f, -0.5f, -0.35f, 0.8f, 0.2f, 0.2f,  0.5f, 0.5f, -0.35f, 0.8f, 0.2f, 0.2f,
             0.5f, 0.5f, -0.35f, 0.8f, 0.2f, 0.2f, -0.5f, 0.5f, -0.35f, 0.8f, 0.2f, 0.2f, -0.5f, -0.5f, -0.35f, 0.8f, 0.2f, 0.2f,
            // Front
            -0.5f, -0.5f,  0.35f, 0.2f, 0.8f, 1.0f,  0.5f, 0.5f,  0.35f, 0.2f, 0.8f, 1.0f,  0.5f, -0.5f,  0.35f, 0.2f, 0.8f, 1.0f,
            -0.5f, -0.5f,  0.35f, 0.2f, 0.8f, 1.0f, -0.5f, 0.5f,  0.35f, 0.2f, 0.8f, 1.0f,  0.5f, 0.5f,  0.35f, 0.2f, 0.8f, 1.0f,
            // Left
            -0.5f, -0.5f, -0.35f, 0.2f, 0.4f, 1.0f, -0.5f, 0.5f,  0.35f, 0.2f, 0.4f, 1.0f, -0.5f, -0.5f,  0.35f, 0.2f, 0.4f, 1.0f,
            -0.5f, -0.5f, -0.35f, 0.2f, 0.4f, 1.0f, -0.5f, 0.5f, -0.35f, 0.2f, 0.4f, 1.0f, -0.5f, 0.5f,  0.35f, 0.2f, 0.4f, 1.0f,
            // Right
             0.5f, -0.5f, -0.35f, 1.0f, 0.5f, 0.2f,  0.5f, -0.5f,  0.35f, 1.0f, 0.5f, 0.2f,  0.5f, 0.5f,  0.35f, 1.0f, 0.5f, 0.2f,
             0.5f, -0.5f, -0.35f, 1.0f, 0.5f, 0.2f,  0.5f, 0.5f,  0.35f, 1.0f, 0.5f, 0.2f,  0.5f, 0.5f, -0.35f, 1.0f, 0.5f, 0.2f,
            // Top
            -0.5f,  0.5f, -0.35f, 0.9f, 0.8f, 0.2f,  0.5f, 0.5f, -0.35f, 0.9f, 0.8f, 0.2f,  0.5f, 0.5f,  0.35f, 0.9f, 0.8f, 0.2f,
            -0.5f,  0.5f, -0.35f, 0.9f, 0.8f, 0.2f,  0.5f, 0.5f,  0.35f, 0.9f, 0.8f, 0.2f, -0.5f, 0.5f,  0.35f, 0.9f, 0.8f, 0.2f,
            // Bottom
            -0.5f, -0.5f, -0.35f, 0.2f, 0.9f, 0.4f, -0.5f, -0.5f,  0.35f, 0.2f, 0.9f, 0.4f,  0.5f, -0.5f,  0.35f, 0.2f, 0.9f, 0.4f,
            -0.5f, -0.5f, -0.35f, 0.2f, 0.9f, 0.4f,  0.5f, -0.5f,  0.35f, 0.2f, 0.9f, 0.4f,  0.5f, -0.5f, -0.35f, 0.2f, 0.9f, 0.4f
        ];

        nint shaderCode = ToUtf8HGlobal(shaderSource);
        nint vertexEntry = ToUtf8HGlobal("vs_main");
        nint fragmentEntry = ToUtf8HGlobal("fs_main");
        try
        {
            var wgslDescriptor = new ShaderModuleWGSLDescriptor
            {
                Code = (byte*)shaderCode
            };
            wgslDescriptor.Chain.SType = SType.ShaderModuleWgslDescriptor;

            var shaderDescriptor = new ShaderModuleDescriptor
            {
                NextInChain = (ChainedStruct*)&wgslDescriptor
            };
            _cubeShader = Runtime.Api.DeviceCreateShaderModule(Device.UnsafeHandle, in shaderDescriptor);
            if (_cubeShader == null)
                throw new InvalidOperationException("WebGPU could not create the cube shader.");

            fixed (float* data = vertices)
            {
                var bufferDescriptor = new BufferDescriptor
                {
                    Size = (ulong)(vertices.Length * sizeof(float)),
                    Usage = BufferUsage.Vertex | BufferUsage.CopyDst,
                    MappedAtCreation = false
                };
                _cubeVertexBuffer = Runtime.Api.DeviceCreateBuffer(Device.UnsafeHandle, in bufferDescriptor);
                if (_cubeVertexBuffer == null)
                    throw new InvalidOperationException("WebGPU could not create the cube vertex buffer.");

                Runtime.Api.QueueWriteBuffer((Queue*)Queue.NativeHandle, _cubeVertexBuffer, 0, data,
                    (nuint)(vertices.Length * sizeof(float)));
            }

            var colorTarget = new ColorTargetState
            {
                Format = _surfaceFormat,
                WriteMask = ColorWriteMask.All
            };
            var fragment = new FragmentState
            {
                Module = _cubeShader,
                EntryPoint = (byte*)fragmentEntry,
                TargetCount = 1,
                Targets = &colorTarget
            };
            VertexAttribute* vertexAttributes = stackalloc VertexAttribute[2];
            vertexAttributes[0] = new VertexAttribute
            {
                Format = VertexFormat.Float32x3,
                Offset = 0,
                ShaderLocation = 0
            };
            vertexAttributes[1] = new VertexAttribute
            {
                Format = VertexFormat.Float32x3,
                Offset = 3 * sizeof(float),
                ShaderLocation = 1
            };
            var vertexBufferLayout = new VertexBufferLayout
            {
                ArrayStride = 6 * sizeof(float),
                StepMode = VertexStepMode.Vertex,
                AttributeCount = 2,
                Attributes = vertexAttributes
            };
            var vertex = new VertexState
            {
                Module = _cubeShader,
                EntryPoint = (byte*)vertexEntry,
                BufferCount = 1,
                Buffers = &vertexBufferLayout
            };
            var primitive = new PrimitiveState
            {
                Topology = PrimitiveTopology.TriangleList,
                FrontFace = FrontFace.Ccw,
                CullMode = CullMode.None
            };
            var depthStencil = new DepthStencilState
            {
                Format = TextureFormat.Depth24Plus,
                DepthWriteEnabled = true,
                DepthCompare = CompareFunction.Less,
                StencilFront = new StencilFaceState { Compare = CompareFunction.Always },
                StencilBack = new StencilFaceState { Compare = CompareFunction.Always }
            };
            var pipelineDescriptor = new RenderPipelineDescriptor
            {
                Layout = _cameraPipelineLayout,
                Vertex = vertex,
                Primitive = primitive,
                DepthStencil = &depthStencil,
                Multisample = new MultisampleState { Count = 1, Mask = 0xFFFFFFFF },
                Fragment = &fragment
            };

            _cubePipeline = Runtime.Api.DeviceCreateRenderPipeline(Device.UnsafeHandle, in pipelineDescriptor);
            if (_cubePipeline == null)
                throw new InvalidOperationException("WebGPU could not create the cube pipeline.");
        }
        finally
        {
            Marshal.FreeHGlobal(shaderCode);
            Marshal.FreeHGlobal(vertexEntry);
            Marshal.FreeHGlobal(fragmentEntry);
        }
    }

    private void CreateUiResources(int width, int height)
    {
        if (_uiTextureView != null) Runtime.Api.TextureViewRelease(_uiTextureView);
        if (_uiTexture != null) { Runtime.Api.TextureDestroy(_uiTexture); Runtime.Api.TextureRelease(_uiTexture); }
        var textureDescriptor = new TextureDescriptor
        {
            Usage = TextureUsage.TextureBinding | TextureUsage.CopyDst,
            Dimension = TextureDimension.Dimension2D,
            Size = new Extent3D { Width = (uint)Math.Max(1, width), Height = (uint)Math.Max(1, height), DepthOrArrayLayers = 1 },
            Format = TextureFormat.Rgba8Unorm, MipLevelCount = 1, SampleCount = 1
        };
        _uiTexture = Runtime.Api.DeviceCreateTexture(Device.UnsafeHandle, in textureDescriptor);
        _uiTextureView = Runtime.Api.TextureCreateView(_uiTexture, null);
        _uiWidth = Math.Max(1, width); _uiHeight = Math.Max(1, height);
        _uiTextureDirty = true;

        if (_uiPipeline != null) Runtime.Api.RenderPipelineRelease(_uiPipeline);
        if (_uiShader != null) Runtime.Api.ShaderModuleRelease(_uiShader);
        if (_uiBindGroup != null) Runtime.Api.BindGroupRelease(_uiBindGroup);
        if (_uiBindGroupLayout != null) Runtime.Api.BindGroupLayoutRelease(_uiBindGroupLayout);
        if (_uiSampler != null) Runtime.Api.SamplerRelease(_uiSampler);
        if (_uiVertexBuffer != null) { Runtime.Api.BufferDestroy(_uiVertexBuffer); Runtime.Api.BufferRelease(_uiVertexBuffer); }

        var samplerDescriptor = new SamplerDescriptor { AddressModeU = AddressMode.ClampToEdge, AddressModeV = AddressMode.ClampToEdge, AddressModeW = AddressMode.ClampToEdge, MagFilter = FilterMode.Linear, MinFilter = FilterMode.Linear, MipmapFilter = MipmapFilterMode.Nearest, LodMaxClamp = 1, MaxAnisotropy = 1 };
        _uiSampler = Runtime.Api.DeviceCreateSampler(Device.UnsafeHandle, in samplerDescriptor);
        BindGroupLayoutEntry* entries = stackalloc BindGroupLayoutEntry[2];
        entries[0] = new BindGroupLayoutEntry { Binding = 0, Visibility = ShaderStage.Fragment, Texture = new TextureBindingLayout { SampleType = TextureSampleType.Float, ViewDimension = TextureViewDimension.Dimension2D } };
        entries[1] = new BindGroupLayoutEntry { Binding = 1, Visibility = ShaderStage.Fragment, Sampler = new SamplerBindingLayout { Type = SamplerBindingType.Filtering } };
        var layoutDescriptor = new BindGroupLayoutDescriptor { EntryCount = 2, Entries = entries };
        _uiBindGroupLayout = Runtime.Api.DeviceCreateBindGroupLayout(Device.UnsafeHandle, in layoutDescriptor);
        BindGroupLayout* layout = _uiBindGroupLayout;
        var pipelineLayoutDescriptor = new PipelineLayoutDescriptor { BindGroupLayoutCount = 1, BindGroupLayouts = &layout };
        var pipelineLayout = Runtime.Api.DeviceCreatePipelineLayout(Device.UnsafeHandle, in pipelineLayoutDescriptor);
        var bindEntries = stackalloc BindGroupEntry[2];
        bindEntries[0] = new BindGroupEntry { Binding = 0, TextureView = _uiTextureView };
        bindEntries[1] = new BindGroupEntry { Binding = 1, Sampler = _uiSampler };
        var bindDescriptor = new BindGroupDescriptor { Layout = _uiBindGroupLayout, EntryCount = 2, Entries = bindEntries };
        _uiBindGroup = Runtime.Api.DeviceCreateBindGroup(Device.UnsafeHandle, in bindDescriptor);

        string shaderSource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "Ui.wgsl"));
        nint code = ToUtf8HGlobal(shaderSource), vertexEntry = ToUtf8HGlobal("vs_main"), fragmentEntry = ToUtf8HGlobal("fs_main");
        try
        {
            var wgsl = new ShaderModuleWGSLDescriptor { Code = (byte*)code }; wgsl.Chain.SType = SType.ShaderModuleWgslDescriptor;
            var shaderDescriptor = new ShaderModuleDescriptor { NextInChain = (ChainedStruct*)&wgsl };
            _uiShader = Runtime.Api.DeviceCreateShaderModule(Device.UnsafeHandle, in shaderDescriptor);
            // The UI texture is premultiplied RGBA (Skia rasterizes with
            // SKAlphaType.Premul) and now carries transparency (the scene is no
            // longer baked into it), so the correct premultiplied blend is
            // src One / dst OneMinusSrcAlpha.
            var blend = new BlendState { Color = new BlendComponent { Operation = BlendOperation.Add, SrcFactor = BlendFactor.One, DstFactor = BlendFactor.OneMinusSrcAlpha }, Alpha = new BlendComponent { Operation = BlendOperation.Add, SrcFactor = BlendFactor.One, DstFactor = BlendFactor.OneMinusSrcAlpha } };
            var target = new ColorTargetState { Format = _surfaceFormat, WriteMask = ColorWriteMask.All, Blend = &blend };
            var fragment = new FragmentState { Module = _uiShader, EntryPoint = (byte*)fragmentEntry, TargetCount = 1, Targets = &target };
            VertexAttribute* attrs = stackalloc VertexAttribute[2];
            attrs[0] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 0, ShaderLocation = 0 };
            attrs[1] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 2 * sizeof(float), ShaderLocation = 1 };
            var vb = new VertexBufferLayout { ArrayStride = 4 * sizeof(float), StepMode = VertexStepMode.Vertex, AttributeCount = 2, Attributes = attrs };
            var vertex = new VertexState { Module = _uiShader, EntryPoint = (byte*)vertexEntry, BufferCount = 1, Buffers = &vb };
            var depthStencil = new DepthStencilState { Format = TextureFormat.Depth24Plus, DepthWriteEnabled = false, DepthCompare = CompareFunction.Always, StencilFront = new StencilFaceState { Compare = CompareFunction.Always }, StencilBack = new StencilFaceState { Compare = CompareFunction.Always } };
            var pipelineDescriptor = new RenderPipelineDescriptor { Layout = pipelineLayout, Vertex = vertex, Primitive = new PrimitiveState { Topology = PrimitiveTopology.TriangleList, FrontFace = FrontFace.Ccw, CullMode = CullMode.None }, DepthStencil = &depthStencil, Multisample = new MultisampleState { Count = 1, Mask = 0xFFFFFFFF }, Fragment = &fragment };
            _uiPipeline = Runtime.Api.DeviceCreateRenderPipeline(Device.UnsafeHandle, in pipelineDescriptor);
            float[] vertices = [-1, -1, 0, 1, 1, -1, 1, 1, 1, 1, 1, 0, 1, 1, 1, 0, -1, 1, 0, 0, -1, -1, 0, 1];
            var bufferDescriptor = new BufferDescriptor { Size = (ulong)(vertices.Length * sizeof(float)), Usage = BufferUsage.Vertex | BufferUsage.CopyDst };
            _uiVertexBuffer = Runtime.Api.DeviceCreateBuffer(Device.UnsafeHandle, in bufferDescriptor);
            fixed (float* data = vertices) Runtime.Api.QueueWriteBuffer((Queue*)Queue.NativeHandle, _uiVertexBuffer, 0, data, (nuint)(vertices.Length * sizeof(float)));
        }
        finally { Marshal.FreeHGlobal(code); Marshal.FreeHGlobal(vertexEntry); Marshal.FreeHGlobal(fragmentEntry); }
        Runtime.Api.PipelineLayoutRelease(pipelineLayout);
    }

    private void UpdateUiTexture(ReadOnlySpan<byte> pixels)
    {
        if (_uiTexture == null) return;
        fixed (byte* data = pixels)
        {
            var destination = new ImageCopyTexture { Texture = _uiTexture };
            var layout = new TextureDataLayout { BytesPerRow = (uint)(_uiWidth * 4), RowsPerImage = (uint)_uiHeight };
            var extent = new Extent3D { Width = (uint)_uiWidth, Height = (uint)_uiHeight, DepthOrArrayLayers = 1 };
            Runtime.Api.QueueWriteTexture((Queue*)Queue.NativeHandle, in destination, data, (nuint)pixels.Length, in layout, in extent);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        if (_sceneBindGroup != null)
            Runtime.Api.BindGroupRelease(_sceneBindGroup);
        if (_sceneTextureView != null)
            Runtime.Api.TextureViewRelease(_sceneTextureView);
        if (_sceneTexture != null)
        {
            Runtime.Api.TextureDestroy(_sceneTexture);
            Runtime.Api.TextureRelease(_sceneTexture);
        }
        if (_backdropBindGroup != null)
            Runtime.Api.BindGroupRelease(_backdropBindGroup);
        if (_backdropParamsBuffer != null)
        {
            Runtime.Api.BufferDestroy(_backdropParamsBuffer);
            Runtime.Api.BufferRelease(_backdropParamsBuffer);
        }
        if (_backdropPipeline != null)
            Runtime.Api.RenderPipelineRelease(_backdropPipeline);
        if (_backdropPipelineLayout != null)
            Runtime.Api.PipelineLayoutRelease(_backdropPipelineLayout);
        if (_backdropBindGroupLayout != null)
            Runtime.Api.BindGroupLayoutRelease(_backdropBindGroupLayout);
        if (_backdropShader != null)
            Runtime.Api.ShaderModuleRelease(_backdropShader);
        if (_cubePipeline != null)
            Runtime.Api.RenderPipelineRelease(_cubePipeline);
        if (_uiPipeline != null)
            Runtime.Api.RenderPipelineRelease(_uiPipeline);
        if (_uiShader != null)
            Runtime.Api.ShaderModuleRelease(_uiShader);
        if (_uiBindGroup != null)
            Runtime.Api.BindGroupRelease(_uiBindGroup);
        if (_uiBindGroupLayout != null)
            Runtime.Api.BindGroupLayoutRelease(_uiBindGroupLayout);
        if (_uiSampler != null)
            Runtime.Api.SamplerRelease(_uiSampler);
        if (_uiVertexBuffer != null)
        {
            Runtime.Api.BufferDestroy(_uiVertexBuffer);
            Runtime.Api.BufferRelease(_uiVertexBuffer);
        }
        if (_uiTextureView != null)
            Runtime.Api.TextureViewRelease(_uiTextureView);
        if (_uiTexture != null)
        {
            Runtime.Api.TextureDestroy(_uiTexture);
            Runtime.Api.TextureRelease(_uiTexture);
        }
        if (_cubeShader != null)
            Runtime.Api.ShaderModuleRelease(_cubeShader);
        if (_cubeVertexBuffer != null)
        {
            Runtime.Api.BufferDestroy(_cubeVertexBuffer);
            Runtime.Api.BufferRelease(_cubeVertexBuffer);
        }
        if (_cameraBindGroup != null)
            Runtime.Api.BindGroupRelease(_cameraBindGroup);
        if (_cameraPipelineLayout != null)
            Runtime.Api.PipelineLayoutRelease(_cameraPipelineLayout);
        if (_cameraBindGroupLayout != null)
            Runtime.Api.BindGroupLayoutRelease(_cameraBindGroupLayout);
        if (_cameraUniformBuffer != null)
        {
            Runtime.Api.BufferDestroy(_cameraUniformBuffer);
            Runtime.Api.BufferRelease(_cameraUniformBuffer);
        }
        if (_depthTextureView != null)
            Runtime.Api.TextureViewRelease(_depthTextureView);
        if (_depthTexture != null)
        {
            Runtime.Api.TextureDestroy(_depthTexture);
            Runtime.Api.TextureRelease(_depthTexture);
        }
        Device.Dispose();
        Adapter.Dispose();
        if (_surface != null)
            Runtime.Api.SurfaceRelease(_surface);
        Runtime.Dispose();
        _disposed = true;
    }
}
