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
    private int _lastMouseX;
    private int _lastMouseY;
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
    private List<UiRectInt> _fullScreenDamage = [];

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

    // GPU decorations (outer box-shadows + uniform solid borders): one
    // instanced quad per region drawn above the UI texture, parameters in a
    // read-only storage buffer plus a viewport-size uniform for the vertex
    // stage (256 regions max).
    private const uint MaxDecorations = 256;
    private ShaderModule* _decoShader;
    private RenderPipeline* _decoPipeline;
    private PipelineLayout* _decoPipelineLayout;
    private BindGroupLayout* _decoBindGroupLayout;
    private BindGroup* _decoBindGroup;
    private Silk.NET.WebGPU.Buffer* _decoParamsBuffer;
    private Silk.NET.WebGPU.Buffer* _decoUniformBuffer;
    private byte[]? _decoParamsBytes;

    // GPU fills (solid panel backgrounds): one instanced quad per region drawn
    // below the UI texture, parameters in a read-only storage buffer plus a
    // viewport-size uniform for the vertex stage (1024 regions max).
    private const uint MaxFills = 1024;
    private ShaderModule* _fillShader;
    private RenderPipeline* _fillPipeline;
    private PipelineLayout* _fillPipelineLayout;
    private BindGroupLayout* _fillBindGroupLayout;
    private BindGroup* _fillBindGroup;
    private Silk.NET.WebGPU.Buffer* _fillParamsBuffer;
    private Silk.NET.WebGPU.Buffer* _fillUniformBuffer;
    private byte[]? _fillParamsBytes;

    [StructLayout(LayoutKind.Sequential)]
    private struct FillGpuParams
    {
        public Vector4 Rect;   // xy = border-box top-left (px), zw = size (px)
        public Vector4 Color;  // straight sRGB RGBA (0..1), a = effective alpha
        public Vector4 Flags;  // x = corner radius (px)
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DecorationGpuParams
    {
        public Vector4 Quad;   // xy = rasterization-bounds top-left (px), zw = size (px)
        public Vector4 Box;    // xy = panel border-box top-left (px), zw = size (px)
        public Vector4 Shape;  // xy = shadow shape top-left (px), zw = size (px)
        public Vector4 Radii;  // x = shape radius, y = blur, z = spread, w = border width
        public Vector4 Color;  // straight sRGB RGBA (0..1), a = effective alpha
        public Vector4 Flags;  // x = kind (0 = outer shadow, 1 = border)
    }

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
            CreateDecorationResources();
            CreateFillResources();
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

        // Rasterize and upload the UI before reading any GPU-composited regions.
        // The renderer recalculates fills, backdrops and decorations while it
        // renders. Reading those lists before Ui.Render() made the compositor
        // use the previous frame's geometry, while the texture already contained
        // the current frame. That one-frame skew is especially visible when an
        // animation changes transform, hover shadows or fill delegation: stale
        // quads then expose/cover the wrong pixels and look like ghosted edges.
        bool uiChanged = false;
        if (Ui is not null)
        {
            uiChanged = _uiTextureDirty || Ui.IsDirty;
            Ui.Render();
            if (uiChanged && Ui.Renderer.PixelBuffer != 0)
            {
                // Skia's premultiplied pixels are uploaded as-is (no CPU
                // conversion): the UI shader un-premultiplies and decodes sRGB.
                // Only the damaged sub-rects are copied, so paint-only changes
                // (hover, caret, animation ticks) upload a handful of small
                // regions instead of the whole 1280x720 texture.
                var damage = _uiTextureDirty ? _fullScreenDamage : Ui.Renderer.DamageRects;
                if (damage.Count > 0)
                {
                    UpdateUiTexture(Ui.Renderer.PixelBuffer, Ui.Renderer.RowBytes, damage);
                    _uiTextureDirty = false;
                }
            }
        }

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
        RenderPipeline* currentPipeline = null;
        // The scene blit and the UI overlay share the UI pipeline. wgpu-native's
        // SetPipeline is comparatively expensive (global lock + validation), so
        // binding the same pipeline twice per frame is avoided: the command
        // stream keeps the last bound pipeline until it changes.
        Runtime.SetPipeline(surfacePass, WebGpuRenderPipeline.FromNative((nint)_uiPipeline));
        currentPipeline = _uiPipeline;
        Runtime.SetBindGroup(surfacePass, WebGpuBindGroup.FromNative((nint)_sceneBindGroup), 0);
        Runtime.SetVertexBuffer(surfacePass, WebGpuBuffer.FromNative((nint)_uiVertexBuffer), (ulong)(6 * 4 * sizeof(float)));
        Runtime.Draw(surfacePass, 6);

        // GPU fills: the solid panel backgrounds the Skia raster skipped (see
        // SkiaUiRenderer.CollectFills) are composited as one instanced quad per
        // region below the UI texture, in paint order. The texture keeps only
        // the non-delegated paint (text, images, fallback fills), so an opaque
        // fill is a plain overwrite of the scene and translucent fills blend
        // over it exactly like the raster would. The renderer only delegates
        // fills whose area is clean of earlier CPU paint, so no texture pixel
        // can hide under a quad.
        var fills = Ui?.Renderer.Fills;
        if (fills is { Count: > 0 } && _fillPipeline != null && _fillBindGroup != null)
        {
            UpdateFillParams(fills);
            if (currentPipeline != _fillPipeline)
            {
                Runtime.SetPipeline(surfacePass, WebGpuRenderPipeline.FromNative((nint)_fillPipeline));
                currentPipeline = _fillPipeline;
            }

            Runtime.SetBindGroup(surfacePass, WebGpuBindGroup.FromNative((nint)_fillBindGroup), 0);
            Runtime.SetVertexBuffer(surfacePass, WebGpuBuffer.FromNative((nint)_uiVertexBuffer), (ulong)(6 * 4 * sizeof(float)));
            Runtime.DrawInstanced(surfacePass, 6, (uint)Math.Min(fills.Count, MaxFills));
        }

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
            currentPipeline = _backdropPipeline;
            Runtime.SetBindGroup(surfacePass, WebGpuBindGroup.FromNative((nint)_backdropBindGroup), 0);
            Runtime.SetVertexBuffer(surfacePass, WebGpuBuffer.FromNative((nint)_uiVertexBuffer), (ulong)(6 * 4 * sizeof(float)));
            Runtime.DrawInstanced(surfacePass, 6, (uint)Math.Min(backdrops.Count, MaxBackdropRegions));
        }

        if (Ui is not null && _uiPipeline != null && _uiBindGroup != null)
        {
            if (currentPipeline != _uiPipeline)
            {
                Runtime.SetPipeline(surfacePass, WebGpuRenderPipeline.FromNative((nint)_uiPipeline));
                currentPipeline = _uiPipeline;
            }

            Runtime.SetBindGroup(surfacePass, WebGpuBindGroup.FromNative((nint)_uiBindGroup), 0);
            Runtime.SetVertexBuffer(surfacePass, WebGpuBuffer.FromNative((nint)_uiVertexBuffer), (ulong)(6 * 4 * sizeof(float)));
            Runtime.Draw(surfacePass, 6);
        }

        // GPU decorations (outer box-shadows + uniform borders): the Skia
        // raster skips them and emits one instanced quad per region, drawn
        // above the UI texture. The renderer only delegates decorations that
        // nothing painted later can cover and that no clip cuts (see
        // SkiaUiRenderer.CollectDecorations), so this reproduces the CSS paint
        // order on top of the flat UI layer.
        var decorations = Ui?.Renderer.Decorations;
        if (decorations is { Count: > 0 } && _decoPipeline != null && _decoBindGroup != null)
        {
            UpdateDecorationParams(decorations);
            if (currentPipeline != _decoPipeline)
            {
                Runtime.SetPipeline(surfacePass, WebGpuRenderPipeline.FromNative((nint)_decoPipeline));
                currentPipeline = _decoPipeline;
            }

            Runtime.SetBindGroup(surfacePass, WebGpuBindGroup.FromNative((nint)_decoBindGroup), 0);
            Runtime.SetVertexBuffer(surfacePass, WebGpuBuffer.FromNative((nint)_uiVertexBuffer), (ulong)(6 * 4 * sizeof(float)));
            Runtime.DrawInstanced(surfacePass, 6, (uint)Math.Min(decorations.Count, MaxDecorations));
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

    private void CreateDecorationResources()
    {
        if (_decoPipelineLayout != null) Runtime.Api.PipelineLayoutRelease(_decoPipelineLayout);
        if (_decoBindGroupLayout != null) Runtime.Api.BindGroupLayoutRelease(_decoBindGroupLayout);
        if (_decoPipeline != null) Runtime.Api.RenderPipelineRelease(_decoPipeline);
        if (_decoShader != null) Runtime.Api.ShaderModuleRelease(_decoShader);
        if (_decoParamsBuffer != null)
        {
            Runtime.Api.BufferDestroy(_decoParamsBuffer);
            Runtime.Api.BufferRelease(_decoParamsBuffer);
        }

        if (_decoUniformBuffer != null)
        {
            Runtime.Api.BufferDestroy(_decoUniformBuffer);
            Runtime.Api.BufferRelease(_decoUniformBuffer);
        }

        var paramsDescriptor = new BufferDescriptor
        {
            Size = (ulong)(MaxDecorations * sizeof(DecorationGpuParams)),
            Usage = BufferUsage.Storage | BufferUsage.CopyDst,
            MappedAtCreation = false
        };
        _decoParamsBuffer = Runtime.Api.DeviceCreateBuffer(Device.UnsafeHandle, in paramsDescriptor);
        _decoParamsBytes = null;

        var uniformDescriptor = new BufferDescriptor
        {
            Size = 16,
            Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
            MappedAtCreation = false
        };
        _decoUniformBuffer = Runtime.Api.DeviceCreateBuffer(Device.UnsafeHandle, in uniformDescriptor);

        BindGroupLayoutEntry* entries = stackalloc BindGroupLayoutEntry[2];
        entries[0] = new BindGroupLayoutEntry
        {
            Binding = 0,
            Visibility = ShaderStage.Vertex | ShaderStage.Fragment,
            Buffer = new BufferBindingLayout { Type = BufferBindingType.ReadOnlyStorage }
        };
        entries[1] = new BindGroupLayoutEntry
        {
            Binding = 1,
            Visibility = ShaderStage.Vertex,
            Buffer = new BufferBindingLayout { Type = BufferBindingType.Uniform }
        };
        var layoutDescriptor = new BindGroupLayoutDescriptor { EntryCount = 2, Entries = entries };
        _decoBindGroupLayout = Runtime.Api.DeviceCreateBindGroupLayout(Device.UnsafeHandle, in layoutDescriptor);
        BindGroupLayout* layout = _decoBindGroupLayout;
        var pipelineLayoutDescriptor = new PipelineLayoutDescriptor { BindGroupLayoutCount = 1, BindGroupLayouts = &layout };
        _decoPipelineLayout = Runtime.Api.DeviceCreatePipelineLayout(Device.UnsafeHandle, in pipelineLayoutDescriptor);
        var bindEntries = stackalloc BindGroupEntry[2];
        bindEntries[0] = new BindGroupEntry { Binding = 0, Buffer = _decoParamsBuffer, Size = (ulong)(MaxDecorations * sizeof(DecorationGpuParams)) };
        bindEntries[1] = new BindGroupEntry { Binding = 1, Buffer = _decoUniformBuffer, Size = 16 };
        var bindDescriptor = new BindGroupDescriptor
        {
            Layout = _decoBindGroupLayout,
            EntryCount = 2,
            Entries = bindEntries
        };
        _decoBindGroup = Runtime.Api.DeviceCreateBindGroup(Device.UnsafeHandle, in bindDescriptor);

        string shaderSource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "Decorations.wgsl"));
        nint code = ToUtf8HGlobal(shaderSource), vertexEntry = ToUtf8HGlobal("vs_main"), fragmentEntry = ToUtf8HGlobal("fs_main");
        try
        {
            var wgsl = new ShaderModuleWGSLDescriptor { Code = (byte*)code };
            wgsl.Chain.SType = SType.ShaderModuleWgslDescriptor;
            var shaderDescriptor = new ShaderModuleDescriptor { NextInChain = (ChainedStruct*)&wgsl };
            _decoShader = Runtime.Api.DeviceCreateShaderModule(Device.UnsafeHandle, in shaderDescriptor);
            // Straight-alpha src-over, matching the UI overlay: the shadow/border
            // color blends over whatever the surface already holds.
            var blend = new BlendState
            {
                Color = new BlendComponent { Operation = BlendOperation.Add, SrcFactor = BlendFactor.SrcAlpha, DstFactor = BlendFactor.OneMinusSrcAlpha },
                Alpha = new BlendComponent { Operation = BlendOperation.Add, SrcFactor = BlendFactor.One, DstFactor = BlendFactor.OneMinusSrcAlpha }
            };
            var target = new ColorTargetState { Format = _surfaceFormat, WriteMask = ColorWriteMask.All, Blend = &blend };
            var fragment = new FragmentState { Module = _decoShader, EntryPoint = (byte*)fragmentEntry, TargetCount = 1, Targets = &target };
            VertexAttribute* attrs = stackalloc VertexAttribute[2];
            attrs[0] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 0, ShaderLocation = 0 };
            attrs[1] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 2 * sizeof(float), ShaderLocation = 1 };
            var vb = new VertexBufferLayout { ArrayStride = 4 * sizeof(float), StepMode = VertexStepMode.Vertex, AttributeCount = 2, Attributes = attrs };
            var vertex = new VertexState { Module = _decoShader, EntryPoint = (byte*)vertexEntry, BufferCount = 1, Buffers = &vb };
            var depthStencil = new DepthStencilState { Format = TextureFormat.Depth24Plus, DepthWriteEnabled = false, DepthCompare = CompareFunction.Always, StencilFront = new StencilFaceState { Compare = CompareFunction.Always }, StencilBack = new StencilFaceState { Compare = CompareFunction.Always } };
            var pipelineDescriptor = new RenderPipelineDescriptor
            {
                Layout = _decoPipelineLayout,
                Vertex = vertex,
                Primitive = new PrimitiveState { Topology = PrimitiveTopology.TriangleList, FrontFace = FrontFace.Ccw, CullMode = CullMode.None },
                DepthStencil = &depthStencil,
                Multisample = new MultisampleState { Count = 1, Mask = 0xFFFFFFFF },
                Fragment = &fragment
            };
            _decoPipeline = Runtime.Api.DeviceCreateRenderPipeline(Device.UnsafeHandle, in pipelineDescriptor);
        }
        finally
        {
            Marshal.FreeHGlobal(code);
            Marshal.FreeHGlobal(vertexEntry);
            Marshal.FreeHGlobal(fragmentEntry);
        }
    }

    private void CreateFillResources()
    {
        if (_fillPipelineLayout != null) Runtime.Api.PipelineLayoutRelease(_fillPipelineLayout);
        if (_fillBindGroupLayout != null) Runtime.Api.BindGroupLayoutRelease(_fillBindGroupLayout);
        if (_fillPipeline != null) Runtime.Api.RenderPipelineRelease(_fillPipeline);
        if (_fillShader != null) Runtime.Api.ShaderModuleRelease(_fillShader);
        if (_fillParamsBuffer != null)
        {
            Runtime.Api.BufferDestroy(_fillParamsBuffer);
            Runtime.Api.BufferRelease(_fillParamsBuffer);
        }

        if (_fillUniformBuffer != null)
        {
            Runtime.Api.BufferDestroy(_fillUniformBuffer);
            Runtime.Api.BufferRelease(_fillUniformBuffer);
        }

        var paramsDescriptor = new BufferDescriptor
        {
            Size = (ulong)(MaxFills * sizeof(FillGpuParams)),
            Usage = BufferUsage.Storage | BufferUsage.CopyDst,
            MappedAtCreation = false
        };
        _fillParamsBuffer = Runtime.Api.DeviceCreateBuffer(Device.UnsafeHandle, in paramsDescriptor);
        _fillParamsBytes = null;

        var uniformDescriptor = new BufferDescriptor
        {
            Size = 16,
            Usage = BufferUsage.Uniform | BufferUsage.CopyDst,
            MappedAtCreation = false
        };
        _fillUniformBuffer = Runtime.Api.DeviceCreateBuffer(Device.UnsafeHandle, in uniformDescriptor);

        BindGroupLayoutEntry* entries = stackalloc BindGroupLayoutEntry[2];
        entries[0] = new BindGroupLayoutEntry
        {
            Binding = 0,
            Visibility = ShaderStage.Vertex | ShaderStage.Fragment,
            Buffer = new BufferBindingLayout { Type = BufferBindingType.ReadOnlyStorage }
        };
        entries[1] = new BindGroupLayoutEntry
        {
            Binding = 1,
            Visibility = ShaderStage.Vertex,
            Buffer = new BufferBindingLayout { Type = BufferBindingType.Uniform }
        };
        var layoutDescriptor = new BindGroupLayoutDescriptor { EntryCount = 2, Entries = entries };
        _fillBindGroupLayout = Runtime.Api.DeviceCreateBindGroupLayout(Device.UnsafeHandle, in layoutDescriptor);
        BindGroupLayout* layout = _fillBindGroupLayout;
        var pipelineLayoutDescriptor = new PipelineLayoutDescriptor { BindGroupLayoutCount = 1, BindGroupLayouts = &layout };
        _fillPipelineLayout = Runtime.Api.DeviceCreatePipelineLayout(Device.UnsafeHandle, in pipelineLayoutDescriptor);
        var bindEntries = stackalloc BindGroupEntry[2];
        bindEntries[0] = new BindGroupEntry { Binding = 0, Buffer = _fillParamsBuffer, Size = (ulong)(MaxFills * sizeof(FillGpuParams)) };
        bindEntries[1] = new BindGroupEntry { Binding = 1, Buffer = _fillUniformBuffer, Size = 16 };
        var bindDescriptor = new BindGroupDescriptor
        {
            Layout = _fillBindGroupLayout,
            EntryCount = 2,
            Entries = bindEntries
        };
        _fillBindGroup = Runtime.Api.DeviceCreateBindGroup(Device.UnsafeHandle, in bindDescriptor);

        string shaderSource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "Fills.wgsl"));
        nint code = ToUtf8HGlobal(shaderSource), vertexEntry = ToUtf8HGlobal("vs_main"), fragmentEntry = ToUtf8HGlobal("fs_main");
        try
        {
            var wgsl = new ShaderModuleWGSLDescriptor { Code = (byte*)code };
            wgsl.Chain.SType = SType.ShaderModuleWgslDescriptor;
            var shaderDescriptor = new ShaderModuleDescriptor { NextInChain = (ChainedStruct*)&wgsl };
            _fillShader = Runtime.Api.DeviceCreateShaderModule(Device.UnsafeHandle, in shaderDescriptor);
            // Straight-alpha src-over, matching the UI overlay: the fill color
            // blends over whatever the surface already holds (the scene).
            var blend = new BlendState
            {
                Color = new BlendComponent { Operation = BlendOperation.Add, SrcFactor = BlendFactor.SrcAlpha, DstFactor = BlendFactor.OneMinusSrcAlpha },
                Alpha = new BlendComponent { Operation = BlendOperation.Add, SrcFactor = BlendFactor.One, DstFactor = BlendFactor.OneMinusSrcAlpha }
            };
            var target = new ColorTargetState { Format = _surfaceFormat, WriteMask = ColorWriteMask.All, Blend = &blend };
            var fragment = new FragmentState { Module = _fillShader, EntryPoint = (byte*)fragmentEntry, TargetCount = 1, Targets = &target };
            VertexAttribute* attrs = stackalloc VertexAttribute[2];
            attrs[0] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 0, ShaderLocation = 0 };
            attrs[1] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 2 * sizeof(float), ShaderLocation = 1 };
            var vb = new VertexBufferLayout { ArrayStride = 4 * sizeof(float), StepMode = VertexStepMode.Vertex, AttributeCount = 2, Attributes = attrs };
            var vertex = new VertexState { Module = _fillShader, EntryPoint = (byte*)vertexEntry, BufferCount = 1, Buffers = &vb };
            var depthStencil = new DepthStencilState { Format = TextureFormat.Depth24Plus, DepthWriteEnabled = false, DepthCompare = CompareFunction.Always, StencilFront = new StencilFaceState { Compare = CompareFunction.Always }, StencilBack = new StencilFaceState { Compare = CompareFunction.Always } };
            var pipelineDescriptor = new RenderPipelineDescriptor
            {
                Layout = _fillPipelineLayout,
                Vertex = vertex,
                Primitive = new PrimitiveState { Topology = PrimitiveTopology.TriangleList, FrontFace = FrontFace.Ccw, CullMode = CullMode.None },
                DepthStencil = &depthStencil,
                Multisample = new MultisampleState { Count = 1, Mask = 0xFFFFFFFF },
                Fragment = &fragment
            };
            _fillPipeline = Runtime.Api.DeviceCreateRenderPipeline(Device.UnsafeHandle, in pipelineDescriptor);
        }
        finally
        {
            Marshal.FreeHGlobal(code);
            Marshal.FreeHGlobal(vertexEntry);
            Marshal.FreeHGlobal(fragmentEntry);
        }
    }

    /// <summary>
    /// Translates the fill regions collected by the Skia pass into the GPU
    /// parameter buffer (Fills.wgsl reads one struct per instance) and uploads
    /// it only when the set actually changed. The viewport uniform is rewritten
    /// every time so the vertex stage maps pixel rects correctly.
    /// </summary>
    private void UpdateFillParams(IReadOnlyList<FillRegion> regions)
    {
        if (_fillParamsBuffer == null || _fillUniformBuffer == null) return;
        var count = Math.Min(regions.Count, (int)MaxFills);
        var bytes = new byte[count * sizeof(FillGpuParams)];
        for (var i = 0; i < count; i++)
        {
            var region = regions[i];
            var p = new FillGpuParams
            {
                Rect = new Vector4(region.X, region.Y, region.Width, region.Height),
                Color = new Vector4(region.Color.R / 255f, region.Color.G / 255f, region.Color.B / 255f, region.Color.A / 255f),
                Flags = new Vector4(region.Radius, 0, 0, 0)
            };
            var offset = i * sizeof(FillGpuParams);
            unsafe
            {
                fixed (byte* data = bytes)
                    *(FillGpuParams*)(data + offset) = p;
            }
        }
        // The viewport is written on every call so a resize is picked up even
        // when the region set itself did not change.
        var viewport = new Vector4(_width, _height, 0, 0);
        Runtime.Api.QueueWriteBuffer((Queue*)Queue.NativeHandle, _fillUniformBuffer, 0, &viewport, 16);
        if (_fillParamsBytes is not null && _fillParamsBytes.AsSpan().SequenceEqual(bytes))
            return;
        _fillParamsBytes = bytes;
        fixed (byte* data = bytes)
        {
            Runtime.Api.QueueWriteBuffer((Queue*)Queue.NativeHandle, _fillParamsBuffer, 0, data,
                (nuint)bytes.Length);
        }
    }

    /// <summary>
    /// Translates the decoration regions collected by the Skia pass into the
    /// GPU parameter buffer (Decorations.wgsl reads one struct per instance)
    /// and uploads it only when the set actually changed. The viewport uniform
    /// is rewritten every time so the vertex stage maps pixel rects correctly.
    /// </summary>
    private void UpdateDecorationParams(IReadOnlyList<DecorationRegion> regions)
    {
        if (_decoParamsBuffer == null || _decoUniformBuffer == null) return;
        var count = Math.Min(regions.Count, (int)MaxDecorations);
        var bytes = new byte[count * sizeof(DecorationGpuParams)];
        for (var i = 0; i < count; i++)
        {
            var region = regions[i];
            var p = new DecorationGpuParams
            {
                Quad = new Vector4(region.QuadX, region.QuadY, region.QuadWidth, region.QuadHeight),
                Box = new Vector4(region.BoxX, region.BoxY, region.BoxWidth, region.BoxHeight),
                Shape = new Vector4(region.ShapeX, region.ShapeY, region.ShapeWidth, region.ShapeHeight),
                Radii = new Vector4(region.ShapeRadius, region.BlurRadius, region.SpreadRadius, region.BorderWidth),
                Color = new Vector4(region.Color.R / 255f, region.Color.G / 255f, region.Color.B / 255f, region.Color.A / 255f),
                Flags = new Vector4((float)region.Kind, 0, 0, 0)
            };
            var offset = i * sizeof(DecorationGpuParams);
            unsafe
            {
                fixed (byte* data = bytes)
                    *(DecorationGpuParams*)(data + offset) = p;
            }
        }
        // The viewport is written on every call so a resize is picked up even
        // when the region set itself did not change.
        var viewport = new Vector4(_width, _height, 0, 0);
        Runtime.Api.QueueWriteBuffer((Queue*)Queue.NativeHandle, _decoUniformBuffer, 0, &viewport, 16);
        if (_decoParamsBytes is not null && _decoParamsBytes.AsSpan().SequenceEqual(bytes))
            return;
        _decoParamsBytes = bytes;
        fixed (byte* data = bytes)
        {
            Runtime.Api.QueueWriteBuffer((Queue*)Queue.NativeHandle, _decoParamsBuffer, 0, data,
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

    /// <summary>
    /// Updates the camera from the real cursor movement while the right button
    /// is held. The cursor remains visible and follows the user's movement;
    /// unlike an FPS-style mouse-look implementation, it is never warped back
    /// to the center of the window.
    /// </summary>
    private void UpdateMouseLook()
    {
        if (!IsKeyDown(0x02)) // VK_RBUTTON
        {
            _mouseLookActive = false;
            return;
        }

        if (!GetCursorPos(out Point cursor))
            return;

        if (!_mouseLookActive)
        {
            _lastMouseX = cursor.X;
            _lastMouseY = cursor.Y;
            _mouseLookActive = true;
            return;
        }

        float deltaX = cursor.X - _lastMouseX;
        float deltaY = cursor.Y - _lastMouseY;
        _lastMouseX = cursor.X;
        _lastMouseY = cursor.Y;
        _cameraYaw += deltaX * 0.003f;
        _cameraPitch = Math.Clamp(_cameraPitch - deltaY * 0.003f, -1.45f, 1.45f);
    }

    private static bool IsKeyDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    private bool IsWindowFocused() => GetForegroundWindow() == _windowHandle;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; public Point(int x, int y) => (X, Y) = (x, y); }

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
            // Plain unorm format: Skia's premultiplied sRGB-encoded bytes are
            // uploaded raw, and Ui.wgsl un-premultiplies + decodes sRGB in the
            // fragment shader. The old sRGB format made the hardware decode the
            // premultiplied values, flattening translucent colors to near-black.
            Format = TextureFormat.Rgba8Unorm, MipLevelCount = 1, SampleCount = 1
        };
        _uiTexture = Runtime.Api.DeviceCreateTexture(Device.UnsafeHandle, in textureDescriptor);
        _uiTextureView = Runtime.Api.TextureCreateView(_uiTexture, null);
        _uiWidth = Math.Max(1, width); _uiHeight = Math.Max(1, height);
        _uiTextureDirty = true;
        _fullScreenDamage = [new UiRectInt(0, 0, _uiWidth, _uiHeight)];

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
            // The UI texture holds straight (un-premultiplied) sRGB RGBA —
            // UpdateUiTexture converts Skia's premultiplied output so the sRGB
            // decode keeps the translucent colors intact instead of flattening
            // them to near-black. The color blend is SrcAlpha over dst, and the
            // alpha channel blends as src over dst.
            var blend = new BlendState { Color = new BlendComponent { Operation = BlendOperation.Add, SrcFactor = BlendFactor.SrcAlpha, DstFactor = BlendFactor.OneMinusSrcAlpha }, Alpha = new BlendComponent { Operation = BlendOperation.Add, SrcFactor = BlendFactor.One, DstFactor = BlendFactor.OneMinusSrcAlpha } };
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

    /// <summary>
    /// Uploads the UI texture from the Skia bitmap's native premultiplied sRGB
    /// pixels. Only the damaged sub-rects are written: each copy uses the
    /// bitmap's row stride as the source layout and the rect origin as the
    /// texture offset, so paint-only changes never touch the full buffer.
    /// The un-premultiply + sRGB decode happens in Ui.wgsl, not on the CPU.
    /// </summary>
    private void UpdateUiTexture(nint pixels, int rowBytes, IReadOnlyList<UiRectInt> damage)
    {
        if (_uiTexture == null || pixels == 0) return;
        foreach (var region in damage)
        {
            var x = Math.Max(0, region.X);
            var y = Math.Max(0, region.Y);
            var width = Math.Min(region.Width, _uiWidth - x);
            var height = Math.Min(region.Height, _uiHeight - y);
            if (width <= 0 || height <= 0) continue;
            var source = pixels + (nint)y * rowBytes + x * 4;
            var destination = new ImageCopyTexture { Texture = _uiTexture, Origin = new Origin3D { X = (uint)x, Y = (uint)y, Z = 0 } };
            var layout = new TextureDataLayout { BytesPerRow = (uint)rowBytes, RowsPerImage = (uint)height };
            var extent = new Extent3D { Width = (uint)width, Height = (uint)height, DepthOrArrayLayers = 1 };
            Runtime.Api.QueueWriteTexture((Queue*)Queue.NativeHandle, in destination, (byte*)source, (nuint)((uint)rowBytes * (uint)height), in layout, in extent);
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
        if (_decoParamsBuffer != null)
        {
            Runtime.Api.BufferDestroy(_decoParamsBuffer);
            Runtime.Api.BufferRelease(_decoParamsBuffer);
        }
        if (_decoUniformBuffer != null)
        {
            Runtime.Api.BufferDestroy(_decoUniformBuffer);
            Runtime.Api.BufferRelease(_decoUniformBuffer);
        }
        if (_decoBindGroup != null)
            Runtime.Api.BindGroupRelease(_decoBindGroup);
        if (_decoBindGroupLayout != null)
            Runtime.Api.BindGroupLayoutRelease(_decoBindGroupLayout);
        if (_decoPipelineLayout != null)
            Runtime.Api.PipelineLayoutRelease(_decoPipelineLayout);
        if (_decoPipeline != null)
            Runtime.Api.RenderPipelineRelease(_decoPipeline);
        if (_decoShader != null)
            Runtime.Api.ShaderModuleRelease(_decoShader);
        if (_fillParamsBuffer != null)
        {
            Runtime.Api.BufferDestroy(_fillParamsBuffer);
            Runtime.Api.BufferRelease(_fillParamsBuffer);
        }
        if (_fillUniformBuffer != null)
        {
            Runtime.Api.BufferDestroy(_fillUniformBuffer);
            Runtime.Api.BufferRelease(_fillUniformBuffer);
        }
        if (_fillBindGroup != null)
            Runtime.Api.BindGroupRelease(_fillBindGroup);
        if (_fillBindGroupLayout != null)
            Runtime.Api.BindGroupLayoutRelease(_fillBindGroupLayout);
        if (_fillPipelineLayout != null)
            Runtime.Api.PipelineLayoutRelease(_fillPipelineLayout);
        if (_fillPipeline != null)
            Runtime.Api.RenderPipelineRelease(_fillPipeline);
        if (_fillShader != null)
            Runtime.Api.ShaderModuleRelease(_fillShader);
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
