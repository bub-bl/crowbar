using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using Crowbar.Engine;
using Crowbar.Engine.Rendering2D;
using Crowbar.UI;

namespace Crowbar.Engine.Rendering;

/// <summary>
/// Runtime renderer. Owns the 3D scene pass (the world's
/// <see cref="MeshRenderer"/> components), the offscreen scene texture and
/// the UI compositing (the GPU-rendered UI layer + backdrop-filter quads),
/// and records every frame through the backend-neutral
/// <see cref="IGraphicsDevice"/> resources. The world hands its mesh
/// renderers to <see cref="Render"/> each frame; this class keeps its own
/// GPU representation (buffers cached per <see cref="Mesh"/>, uniforms per
/// component) like Unreal's FPrimitiveSceneProxy. Camera state and input live
/// outside this class; the runtime hands a <see cref="Camera"/> and the
/// <see cref="UiSystem"/> to <see cref="Render"/> each frame. Nothing here
/// references WebGPU (or any other graphics API).
/// </summary>
public sealed class Renderer : IDisposable
{
    // Mirrors SceneUniforms in Shaders/Common/Transform.wgsl: view, projection,
    // camera position and the clock. Written once per frame, shared by every
    // mesh pipeline through bind group 0.
    [StructLayout(LayoutKind.Sequential)]
    private struct SceneUniforms
    {
        public Matrix4x4 View;
        public Matrix4x4 Projection;
        public Vector4 CameraPosition;
        public Vector4 Time;
    }

    // Mirrors LightData in Shaders/Common/Lighting.wgsl.
    [StructLayout(LayoutKind.Sequential)]
    private struct LightGpuData
    {
        public Vector4 PositionType;     // xyz = position, w = 0 directional / 1 point
        public Vector4 ColorIntensity;   // rgb = color, w = intensity
        public Vector4 DirectionRange;   // xyz = direction, w = range (point lights)
    }

    // Mirrors LightsUniform in Shaders/Common/Lighting.wgsl: a u32 count
    // padded to 16 bytes, then array<LightData, 8> (48 bytes per element).
    private const int LightsBufferSize = 16 + MaxLights * 48;

    /// <summary>GPU geometry of one <see cref="Mesh"/>, shared by every renderable using it.</summary>
    private sealed class MeshBuffers
    {
        public required IBuffer VertexBuffer { get; init; }
        public required IBuffer IndexBuffer { get; init; }
    }

    /// <summary>
    /// Per-renderable GPU state: the model matrix buffer, the material
    /// parameters buffer (packed from the shader's material struct) and the
    /// group-1 bind group referencing them plus the material's textures. The
    /// shader/technique it was built for is remembered so a material change
    /// rebuilds the resources instead of reusing a stale bind group.
    /// </summary>
    private sealed class RenderableResources
    {
        public required Shader Shader { get; init; }
        public required string Technique { get; init; }
        public required IBuffer ModelBuffer { get; init; }
        public IBuffer? MaterialBuffer { get; init; }
        public required IBindGroup BindGroup { get; init; }
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

    // Mirrors OutlineParams in Shaders/SelectionOutline.wgsl.
    [StructLayout(LayoutKind.Sequential)]
    private struct SelectionOutlineParams
    {
        public Vector4 Color;      // rgb = outline color, a = opacity
        public Vector2 TexelSize;  // 1/width, 1/height
        public float Thickness;    // outline radius in pixels
        public float Padding;
    }

    private readonly IGraphicsDevice _device;
    private int _width;
    private int _height;
    private bool _disposed;

    // The rectangle of the surface (in framebuffer pixels, top-left origin)
    // the 3D scene renders into. The host (the editor) points it at its 3D
    // viewport panel; null means "fill the whole window" (the pre-viewport
    // behaviour, still correct for a game-only host).
    private UiRect? _sceneViewport;

    private UiRect SceneViewport =>
        _sceneViewport is { Width: > 0, Height: > 0 } viewport
            ? viewport
            : new UiRect(0, 0, _width, _height);

    /// <summary>
    /// Sets the on-surface rectangle the 3D scene renders into (framebuffer
    /// pixels, top-left origin). Pass null to fill the whole window, which is
    /// the default and the right choice for a host without a docked viewport.
    /// </summary>
    public void SetSceneViewport(UiRect? viewport) => _sceneViewport = viewport;

    // Camera: the per-frame view/projection/position, written into the shared
    // scene buffer each frame.
    private SceneUniforms _scene;

    // Mesh scene pass. Group 0 holds the per-frame scene + lights buffers;
    // group 1 is per-renderable (model, material, textures) and its layout is
    // derived from the shader's own bindings, so adding a binding to a WGSL
    // file requires no C# change. Pipelines are cached per (shader, technique),
    // GPU geometry per Mesh, renderable state per component.
    private const int MaxLights = 8;
    private static readonly BindGroupLayoutBinding[] SceneGroupBindings =
    [
        new() { Slot = 0, Type = BindingType.UniformBuffer, Stages = ShaderStage.Vertex | ShaderStage.Fragment },
        new() { Slot = 1, Type = BindingType.UniformBuffer, Stages = ShaderStage.Fragment }
    ];
    private static readonly VertexBufferLayoutDescription MeshVertexLayout = new()
    {
        // position (3) + normal (3) + tangent (4) + uv (2).
        Stride = 12 * sizeof(float),
        Attributes =
        [
            new VertexAttributeDescription { Format = VertexFormat.Float32x3, Offset = 0, ShaderLocation = 0 },
            new VertexAttributeDescription { Format = VertexFormat.Float32x3, Offset = 3 * sizeof(float), ShaderLocation = 1 },
            new VertexAttributeDescription { Format = VertexFormat.Float32x4, Offset = 6 * sizeof(float), ShaderLocation = 2 },
            new VertexAttributeDescription { Format = VertexFormat.Float32x2, Offset = 10 * sizeof(float), ShaderLocation = 3 }
        ]
    };
    private IBuffer _sceneBuffer = null!;
    private IBuffer _lightsBuffer = null!;
    private ISampler _materialSampler = null!;
    private ITexture _defaultWhiteTexture = null!;
    private ITexture _defaultBlackTexture = null!;
    private ITexture _defaultNormalTexture = null!;
    private readonly Dictionary<(Shader Shader, string Technique), IPipeline> _meshPipelines = [];
    private readonly Dictionary<IPipeline, IBindGroup> _sceneBindGroups = [];
    private readonly Dictionary<Mesh, MeshBuffers> _meshBuffers = [];
    private readonly Dictionary<MeshRenderer, RenderableResources> _renderables = [];
    private readonly Dictionary<Texture2D, ITexture> _materialTextures = [];
    private Material? _defaultMaterial;

    // Editor ground grid: a fullscreen pass drawn after the meshes inside the
    // scene pass (tests mesh depth without writing it), configurable through
    // the Grid property.
    public Grid Grid { get; } = new();
    private IPipeline _gridPipeline = null!;
    private IBuffer _gridVertexBuffer = null!;
    private IBuffer _gridUniformBuffer = null!;
    private IBindGroup _gridBindGroup = null!;

    // Viewport gizmos: the translation widget around the selection plus the
    // billboard sprites (lights, selection ring). Pure overlay, drawn after
    // the grid, never part of the world.
    public GizmoRenderer Gizmos { get; private set; } = null!;

    // Selection outline: a real post-process contour around the selected
    // entity. The selected mesh renders into a mask texture (depth-tested
    // against the scene), then a fullscreen pass dilates that mask and tints
    // the silhouette's edge in the surface composite.
    public SelectionOutline Outline { get; } = new();
    private ITexture _selectionTexture = null!;
    private IPipeline _selectionMaskPipeline = null!;
    private IBindGroup _selectionMaskSceneBindGroup = null!;
    private IBindGroup _selectionMaskModelBindGroup = null!;
    private IBuffer _selectionMaskModelBuffer = null!;
    private IPipeline _outlinePipeline = null!;
    private IBindGroup _outlineBindGroup = null!;
    private IBuffer _outlineParamsBuffer = null!;

    // Offscreen 3D scene: the cube renders here instead of directly on the
    // surface, then the scene is blitted to the surface. backdrop-filter
    // panels are composited on the GPU by Backdrop.wgsl sampling this texture
    // directly (like S&box's ui_backdropfilter.shader), so the CPU never sees
    // the scene and the UI layer only re-records when the UI changes.
    private ITexture _sceneTexture = null!;
    private IBindGroup _sceneBindGroup = null!;

    private ITexture _depthTexture = null!;

    // Scene blit: the UI pipeline (Ui.wgsl) also blits the offscreen scene
    // texture onto the surface, so it is kept even though the Skia UI texture
    // upload path is gone.
    private ISampler _uiSampler = null!;
    private IPipeline _uiPipeline = null!;
    private IBuffer _uiVertexBuffer = null!;

    // GPU UI renderer: the tree-walk painter records the panel tree into a
    // Renderer2D, which draws the UI offscreen on the GPU (no Skia raster).
    // The surface pass blits that target over the scene with Ui2D.wgsl.
    private Renderer2D _ui2d = null!;
    private UiTreePainter _uiPainter = null!;
    private IPipeline _ui2dPipeline = null!;
    private IBindGroup _ui2dBindGroup = null!;
    private ISampler _ui2dSampler = null!;
    private ITexture? _ui2dTextureBound;

    // Backdrop compositor (16 regions max).
    private const uint MaxBackdropRegions = 16;
    private IPipeline _backdropPipeline = null!;
    private IBindGroup _backdropBindGroup = null!;
    private IBuffer _backdropParamsBuffer = null!;
    private byte[]? _backdropParamsBytes;

    public Renderer(IGraphicsDevice device)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _width = device.Width;
        _height = device.Height;

        CreateMeshResources();
        CreateGridResources();
        Gizmos = new GizmoRenderer(_device, _sceneBuffer, (ulong)sizeof(SceneUniforms));
        CreateBackdropResources();
        CreateUiResources();
        CreateDepthTexture(_width, _height);
        CreateSceneResources(_width, _height);
        CreateSelectionOutlineResources(_width, _height);
        CreateUi2DResources();
        _ui2d = new Renderer2D(_device);
        _uiPainter = new UiTreePainter(_ui2d);
        UpdateCamera(new Camera());
    }

    public void Render(World? world, Camera camera, double time, UiSystem ui)
    {
        if (_disposed)
            return;

        UpdateCamera(camera);

        var viewport = SceneViewport;

        ITexture? frame = _device.Swapchain.AcquireTexture();
        if (frame is null)
            return;

        using (frame)
        {
            using ICommandBuffer commandBuffer = _device.CreateCommandBuffer();

            // Pass 1: render the 3D scene into the offscreen scene texture (it is
            // both blitted to the surface and copied back for the UI backdrop).
            var scenePassDescription = new RenderPassDescription
            {
                Color = new ColorAttachment
                {
                    Texture = _sceneTexture,
                    LoadOp = RenderAttachmentLoadOp.Clear,
                    StoreOp = RenderAttachmentStoreOp.Store,
                    ClearColor = new Vector4(0.06f, 0.09f, 0.16f, 1f)
                },
                Depth = new DepthAttachment
                {
                    Texture = _depthTexture,
                    LoadOp = RenderAttachmentLoadOp.Clear,
                    StoreOp = RenderAttachmentStoreOp.Store,
                    ClearValue = 1f
                }
            };
            using (IRenderPass scenePass = commandBuffer.BeginRenderPass(scenePassDescription))
            {
                // The 3D scene renders only inside the host's viewport
                // rectangle. The load-op clear above covers the whole texture
                // (scissors do not clip it), so the area outside the viewport
                // stays the clear color while the viewport holds the scene.
                ApplySceneViewport(scenePass, viewport);
                DrawMeshRenderers(scenePass, world, time);
                DrawGrid(scenePass);
                Gizmos.Draw(scenePass, world, camera, (int)viewport.Width, (int)viewport.Height);
            }

            // Pass 1b: render the selected entity into the selection mask
            // (flat white, depth-tested against the scene depth, so the
            // outline hugs the visible silhouette). Skipped entirely when
            // nothing mesh-shaped is selected.
            var outlineActive = IsSelectionOutlineActive();
            if (outlineActive)
            {
                using (IRenderPass maskPass = commandBuffer.BeginRenderPass(new RenderPassDescription
                {
                    Color = new ColorAttachment
                    {
                        Texture = _selectionTexture,
                        LoadOp = RenderAttachmentLoadOp.Clear,
                        StoreOp = RenderAttachmentStoreOp.Store,
                        ClearColor = Vector4.Zero
                    },
                    Depth = new DepthAttachment
                    {
                        Texture = _depthTexture,
                        LoadOp = RenderAttachmentLoadOp.Load,
                        StoreOp = RenderAttachmentStoreOp.Store
                    }
                }))
                {
                    ApplySceneViewport(maskPass, viewport);
                    DrawSelectionMask(maskPass);
                }
            }

            // Record the UI tree into the GPU renderer: the painter walks the
            // laid-out tree and the Renderer2D draws it offscreen. The Renderer2D
            // owns its own command buffer and submits here (before the surface
            // pass), so its target texture is ready when the surface blit samples it.
            if (ui is not null)
            {
                // Prepare runs the style/layout passes; false = nothing changed, so
                // the previous frame's target is still current and the CPU tree walk
                // is skipped (idle frames cost nothing).
                var changed = ui.Prepare();
                if (changed || _ui2d.Target is null)
                {
                    _uiPainter.SetTooltip(ui.Renderer.TooltipText, new Vector2(ui.Renderer.TooltipX, ui.Renderer.TooltipY));
                    _uiPainter.Paint(ui.Screen);
                }

                var target = _ui2d.Target;
                if (target is not null && !ReferenceEquals(_ui2dTextureBound, target))
                {
                    _ui2dBindGroup?.Dispose();
                    _ui2dBindGroup = _ui2dPipeline.CreateBindGroup(
                    [
                        new BindGroupBinding { Slot = 0, Texture = target },
                        new BindGroupBinding { Slot = 1, Sampler = _ui2dSampler }
                    ]);
                    _ui2dTextureBound = target;
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
                    Texture = frame,
                    LoadOp = RenderAttachmentLoadOp.Clear,
                    StoreOp = RenderAttachmentStoreOp.Store,
                    ClearColor = new Vector4(0.06f, 0.09f, 0.16f, 1f)
                },
                Depth = new DepthAttachment
                {
                    Texture = _depthTexture,
                    LoadOp = RenderAttachmentLoadOp.Clear,
                    StoreOp = RenderAttachmentStoreOp.Store,
                    ClearValue = 1f
                }
            };
            using (IRenderPass surfacePass = commandBuffer.BeginRenderPass(surfacePassDescription))
            {
                // The scene blit and the UI overlay share the UI pipeline.
                // wgpu-native's SetPipeline is comparatively expensive (global lock
                // + validation), so binding the same pipeline twice per frame is
                // avoided: the command stream keeps the last bound pipeline until
                // it changes.
                IPipeline currentPipeline = _uiPipeline;
                if (outlineActive)
                {
                    // The selection outline pass replaces the plain scene blit.
                    UpdateOutlineParams();
                    surfacePass.SetPipeline(_outlinePipeline);
                    currentPipeline = _outlinePipeline;
                    surfacePass.SetBindGroup(_outlineBindGroup, 0);
                    surfacePass.SetVertexBuffer(_uiVertexBuffer, 6 * 4 * sizeof(float));
                    surfacePass.Draw(6);
                }
                else
                {
                    surfacePass.SetPipeline(_uiPipeline);
                    surfacePass.SetBindGroup(_sceneBindGroup, 0);
                    surfacePass.SetVertexBuffer(_uiVertexBuffer, 6 * 4 * sizeof(float));
                    surfacePass.Draw(6);
                }

                // backdrop-filter: one instanced fullscreen quad per region,
                // sampling the 3D scene texture with blur + color transforms. The
                // regions come from the tree-walk painter (which defers expressible
                // backdrop-filters to the compositor); the
                // UI overlay is drawn on top afterwards (regions are painted between
                // the scene and the UI, like S&box's ui_backdropfilter).
                var backdrops = _uiPainter.Backdrops;
                if (backdrops is { Count: > 0 })
                {
                    UpdateBackdropParams(backdrops);
                    surfacePass.SetPipeline(_backdropPipeline);
                    currentPipeline = _backdropPipeline;
                    surfacePass.SetBindGroup(_backdropBindGroup, 0);
                    surfacePass.SetVertexBuffer(_uiVertexBuffer, 6 * 4 * sizeof(float));
                    surfacePass.DrawInstanced(6, (uint)Math.Min(backdrops.Count, MaxBackdropRegions));
                }

                // The GPU-rendered UI layer (Renderer2D offscreen target) blits over
                // the scene + backdrop regions. It is transparent wherever the UI is
                // empty, so the scene shows through.
                if (ui is not null && _ui2dBindGroup is not null)
                {
                    if (currentPipeline != _ui2dPipeline)
                    {
                        surfacePass.SetPipeline(_ui2dPipeline);
                        currentPipeline = _ui2dPipeline;
                    }

                    surfacePass.SetBindGroup(_ui2dBindGroup, 0);
                    surfacePass.SetVertexBuffer(_uiVertexBuffer, 6 * 4 * sizeof(float));
                    surfacePass.Draw(6);
                }
            }

            commandBuffer.Submit();
            _device.Swapchain.Present();
        }
    }

    public void Resize(int width, int height)
    {
        if (_disposed)
            return;

        _device.Resize(width, height);
        _width = Math.Max(1, width);
        _height = Math.Max(1, height);
        CreateDepthTexture(_width, _height);
        CreateSceneResources(_width, _height);
        CreateSelectionOutlineResources(_width, _height);
    }

    private void UpdateCamera(Camera camera)
    {
        var viewport = SceneViewport;
        float aspect = Math.Max(1, viewport.Width) / (float)Math.Max(1, viewport.Height);
        _scene = new SceneUniforms
        {
            View = camera.ViewMatrix,
            Projection = camera.ProjectionMatrix(aspect),
            CameraPosition = new Vector4(camera.Position, 1f),
            Time = new Vector4(0f, 0f, 0f, 0f)
        };
    }

    /// <summary>
    /// Constrains a 3D pass (scene or selection mask) to the host's viewport
    /// rectangle, clamped to the framebuffer so a partially off-screen panel
    /// (mid-drag dock layout) never produces an out-of-bounds scissor.
    /// </summary>
    private void ApplySceneViewport(IRenderPass pass, UiRect viewport)
    {
        var x = Math.Max(0, viewport.X);
        var y = Math.Max(0, viewport.Y);
        var width = Math.Max(1, Math.Min(_width, viewport.X + Math.Max(1, viewport.Width)) - x);
        var height = Math.Max(1, Math.Min(_height, viewport.Y + Math.Max(1, viewport.Height)) - y);

        pass.SetViewport(x, y, width, height);
        pass.SetScissorRect((uint)x, (uint)y, (uint)width, (uint)height);
    }

    private void CreateMeshResources()
    {
        _sceneBuffer = _device.CreateBuffer(new BufferDescription
        {
            Size = (ulong)sizeof(SceneUniforms),
            Usage = BufferUsage.Uniform | BufferUsage.CopyDst
        });
        _lightsBuffer = _device.CreateBuffer(new BufferDescription
        {
            Size = (ulong)LightsBufferSize,
            Usage = BufferUsage.Uniform | BufferUsage.CopyDst
        });
        _materialSampler = _device.CreateSampler(new SamplerDescription
        {
            AddressMode = SamplerAddressMode.Repeat
        });

        // 1x1 fallbacks for texture slots the material does not bind. Flat
        // blue normals and black emissive keep PBR correct with no textures.
        _defaultWhiteTexture = CreateSolidTexture(255, 255, 255, 255, srgb: false);
        _defaultBlackTexture = CreateSolidTexture(0, 0, 0, 255, srgb: true);
        _defaultNormalTexture = CreateSolidTexture(128, 128, 255, 255, srgb: false);

        _defaultMaterial = Material.CreateDefault(Shader.Load(Path.Combine("Shaders", "Mesh.wgsl")));
    }

    /// <summary>Creates a 1x1 texture with a single RGBA pixel.</summary>
    private ITexture CreateSolidTexture(byte r, byte g, byte b, byte a, bool srgb)
    {
        var texture = _device.CreateTexture(new TextureDescription
        {
            Width = 1,
            Height = 1,
            Format = srgb ? TextureFormat.Rgba8UnormSrgb : TextureFormat.Rgba8Unorm,
            Sampled = true,
            CopyDestination = true
        });
        byte[] pixel = [r, g, b, a];
        unsafe
        {
            fixed (byte* data = pixel)
                texture.Write((nint)data, 4, 0, 0, 1, 1);
        }
        return texture;
    }

    private void CreateGridResources()
    {
        // Fullscreen quad (position only) drawn as a triangle list; the vertex
        // shader unprojects the near/far planes and the fragment shader
        // intersects the view ray with the ground plane.
        float[] vertices =
        [
             1f,  1f, 0f,
            -1f, -1f, 0f,
            -1f,  1f, 0f,
             1f,  1f, 0f,
             1f, -1f, 0f,
            -1f, -1f, 0f
        ];
        _gridVertexBuffer = _device.CreateBuffer(new BufferDescription
        {
            Size = (ulong)(vertices.Length * sizeof(float)),
            Usage = BufferUsage.Vertex | BufferUsage.CopyDst
        });
        unsafe
        {
            fixed (float* data = vertices)
                _gridVertexBuffer.Write(new ReadOnlySpan<byte>(data, vertices.Length * sizeof(float)));
        }

        _gridUniformBuffer = _device.CreateBuffer(new BufferDescription
        {
            Size = (ulong)sizeof(GridUniforms),
            Usage = BufferUsage.Uniform | BufferUsage.CopyDst
        });

        string shaderSource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "Grid.wgsl"));
        _gridPipeline = _device.CreatePipeline(new PipelineDescription
        {
            ShaderSource = shaderSource,
            VertexEntryPoint = "vs_main",
            FragmentEntryPoint = "fs_main",
            ColorFormat = _device.Swapchain.Format,
            DepthFormat = TextureFormat.Depth24Plus,
            // Drawn after the meshes: it tests their depth but must not write
            // depth, and the lines blend over the scene.
            AlphaBlend = true,
            DepthWriteEnabled = false,
            DepthCompare = CompareFunction.LessEqual,
            VertexLayout = new VertexBufferLayoutDescription
            {
                Stride = 3 * sizeof(float),
                Attributes =
                [
                    new VertexAttributeDescription { Format = VertexFormat.Float32x3, Offset = 0, ShaderLocation = 0 }
                ]
            },
            BindGroups =
            [
                [
                    new BindGroupLayoutBinding
                    {
                        Slot = 0,
                        Type = BindingType.UniformBuffer,
                        Stages = ShaderStage.Vertex | ShaderStage.Fragment
                    }
                ]
            ]
        });
        _gridBindGroup = _gridPipeline.CreateBindGroup(
        [
            new BindGroupBinding { Slot = 0, Buffer = _gridUniformBuffer, BufferSize = (ulong)sizeof(GridUniforms) }
        ]);
    }

    /// <summary>
    /// Draws the ground grid after the world's meshes. It shares the scene
    /// camera and is composited into the same offscreen scene texture, so
    /// backdrop-filter panels and the surface blit see it like any 3D content.
    /// </summary>
    private void DrawGrid(IRenderPass pass)
    {
        var uniforms = Grid.CreateUniforms(_scene.View, _scene.Projection);
        _gridUniformBuffer.Write(in uniforms);

        pass.SetPipeline(_gridPipeline);
        pass.SetBindGroup(_gridBindGroup, 0);
        pass.SetVertexBuffer(_gridVertexBuffer, _gridVertexBuffer.Size);
        pass.Draw(6);
    }

    /// <summary>Draws every living <see cref="MeshRenderer"/> in the world at its world transform.</summary>
    private void DrawMeshRenderers(IRenderPass pass, World? world, double time)
    {
        if (world is null)
            return;

        // Materialize once: components may be destroyed while we draw.
        var renderers = world.Query<MeshRenderer>().ToList();
        if (renderers.Count == 0)
            return;

        UpdateSceneUniforms(world, time);

        // Release GPU state for renderables whose component was destroyed.
        foreach (var stale in _renderables.Keys.Except(renderers).ToArray())
            DisposeRenderable(stale);

        IPipeline? currentPipeline = null;
        foreach (var renderer in renderers)
        {
            if (!renderer.IsValid || renderer.Model is null)
                continue;

            var material = renderer.Material ?? _defaultMaterial!;
            var pipeline = GetMeshPipeline(material.Shader, material.Technique);
            var renderable = GetRenderableResources(renderer, material, pipeline);

            if (currentPipeline != pipeline)
            {
                pass.SetPipeline(pipeline);
                pass.SetBindGroup(GetSceneBindGroup(pipeline), 0);
                currentPipeline = pipeline;
            }
            pass.SetBindGroup(renderable.BindGroup, 1);

            var modelMatrix = ToWorldMatrix(renderer.World);
            renderable.ModelBuffer.Write(in modelMatrix);
            if (renderable.MaterialBuffer is not null)
            {
                var packed = UniformPacker.Pack(material.Shader.MaterialFields, material.Values);
                renderable.MaterialBuffer.Write(packed);
            }

            foreach (var mesh in renderer.Model.Meshes)
            {
                var buffers = GetMeshBuffers(mesh);
                pass.SetVertexBuffer(buffers.VertexBuffer, buffers.VertexBuffer.Size);
                pass.SetIndexBuffer(buffers.IndexBuffer, buffers.IndexBuffer.Size);
                pass.DrawIndexed((uint)mesh.Indices.Length);
            }
        }
    }

    /// <summary>
    /// Writes the shared scene uniforms (view/projection/camera/clock) and
    /// packs the world's lights (directional + point, capped at
    /// <see cref="MaxLights"/>) into the light buffer.
    /// </summary>
    private void UpdateSceneUniforms(World? world, double time)
    {
        _scene.Time = new Vector4((float)time, 0f, 0f, 0f);
        _sceneBuffer.Write(in _scene);

        // Collect the world's lights (directional + point, capped at
        // MaxLights), then lay them out exactly as Lighting.wgsl expects:
        // count at offset 0, array<LightData, 8> at offset 16.
        var collected = new LightGpuData[MaxLights];
        var count = 0;
        if (world is not null)
        {
            foreach (var light in world.Query<Light>())
            {
                if (!light.Enabled || count >= MaxLights)
                    continue;

                switch (light)
                {
                    case PointLight point:
                        collected[count] = new LightGpuData
                        {
                            PositionType = new Vector4(point.World.Position, 1f),
                            ColorIntensity = new Vector4(point.Color, point.Intensity),
                            DirectionRange = new Vector4(0f, 0f, 0f, point.Range)
                        };
                        break;
                    case DirectionalLight directional:
                        collected[count] = new LightGpuData
                        {
                            PositionType = new Vector4(0f, 0f, 0f, 0f),
                            ColorIntensity = new Vector4(directional.Color, directional.Intensity),
                            DirectionRange = new Vector4(directional.Direction, 0f)
                        };
                        break;
                    default:
                        continue;
                }

                count++;
            }
        }

        var bytes = new byte[LightsBufferSize];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)count);
        unsafe
        {
            fixed (byte* destination = bytes)
            {
                var lightPtr = (LightGpuData*)(destination + 16);
                for (var i = 0; i < count; i++)
                    lightPtr[i] = collected[i];
            }
        }
        _lightsBuffer.Write(bytes);
    }

    /// <summary>
    /// Returns (creating on first use) the pipeline for a shader and technique.
    /// The group-1 layout is derived from the shader's own bindings, so the
    /// WGSL source is the single source of truth for the pipeline layout.
    /// </summary>
    private IPipeline GetMeshPipeline(Shader shader, string techniqueName)
    {
        var key = (shader, techniqueName);
        if (_meshPipelines.TryGetValue(key, out var existing))
            return existing;

        var technique = shader.GetTechnique(techniqueName);
        var group1 = shader.Bindings
            .Where(binding => binding.Group == 1)
            .OrderBy(binding => binding.Slot)
            .Select(binding => new BindGroupLayoutBinding
            {
                Slot = binding.Slot,
                Type = binding.Kind switch
                {
                    ShaderBindingKind.UniformBuffer => BindingType.UniformBuffer,
                    ShaderBindingKind.ReadOnlyStorageBuffer => BindingType.ReadOnlyStorageBuffer,
                    ShaderBindingKind.Texture => BindingType.Texture,
                    ShaderBindingKind.Sampler => BindingType.Sampler,
                    _ => throw new ArgumentOutOfRangeException()
                },
                Stages = binding.Kind == ShaderBindingKind.UniformBuffer
                    ? ShaderStage.Vertex | ShaderStage.Fragment
                    : ShaderStage.Fragment
            })
            .ToList();

        var pipeline = _device.CreatePipeline(new PipelineDescription
        {
            ShaderSource = shader.Source,
            VertexEntryPoint = technique.VertexEntryPoint,
            FragmentEntryPoint = technique.FragmentEntryPoint,
            ColorFormat = _device.Swapchain.Format,
            DepthFormat = TextureFormat.Depth24Plus,
            DepthWriteEnabled = true,
            DepthCompare = CompareFunction.Less,
            VertexLayout = MeshVertexLayout,
            BindGroups = [SceneGroupBindings, group1]
        });
        _meshPipelines.Add(key, pipeline);
        return pipeline;
    }

    /// <summary>Creates (or returns) the per-frame bind group 0 for a mesh pipeline.</summary>
    private IBindGroup GetSceneBindGroup(IPipeline pipeline)
    {
        if (_sceneBindGroups.TryGetValue(pipeline, out var existing))
            return existing;

        var bindGroup = pipeline.CreateBindGroup(0,
        [
            new BindGroupBinding { Slot = 0, Buffer = _sceneBuffer, BufferSize = (ulong)sizeof(SceneUniforms) },
            new BindGroupBinding { Slot = 1, Buffer = _lightsBuffer, BufferSize = (ulong)LightsBufferSize }
        ]);
        _sceneBindGroups.Add(pipeline, bindGroup);
        return bindGroup;
    }

    /// <summary>Uploads one mesh's geometry once; shared by every renderable using the same mesh.</summary>
    private MeshBuffers GetMeshBuffers(Mesh mesh)
    {
        if (_meshBuffers.TryGetValue(mesh, out var existing))
            return existing;

        // Mesh shaders consume interleaved position + normal + tangent + uv
        // floats (stride 48), matching MeshVertexLayout.
        var floats = new float[mesh.Vertices.Length * 12];
        for (var i = 0; i < mesh.Vertices.Length; i++)
        {
            var vertex = mesh.Vertices[i];
            var offset = i * 12;
            floats[offset] = vertex.Position.X;
            floats[offset + 1] = vertex.Position.Y;
            floats[offset + 2] = vertex.Position.Z;
            floats[offset + 3] = vertex.Normal.X;
            floats[offset + 4] = vertex.Normal.Y;
            floats[offset + 5] = vertex.Normal.Z;
            floats[offset + 6] = vertex.Tangent.X;
            floats[offset + 7] = vertex.Tangent.Y;
            floats[offset + 8] = vertex.Tangent.Z;
            floats[offset + 9] = vertex.Tangent.W;
            floats[offset + 10] = vertex.TexCoord.X;
            floats[offset + 11] = vertex.TexCoord.Y;
        }

        var vertexBuffer = _device.CreateBuffer(new BufferDescription
        {
            Size = (ulong)(floats.Length * sizeof(float)),
            Usage = BufferUsage.Vertex | BufferUsage.CopyDst
        });
        unsafe
        {
            fixed (float* data = floats)
                vertexBuffer.Write(new ReadOnlySpan<byte>(data, floats.Length * sizeof(float)));
        }

        var indexBuffer = _device.CreateBuffer(new BufferDescription
        {
            Size = (ulong)(mesh.Indices.Length * sizeof(uint)),
            Usage = BufferUsage.Index | BufferUsage.CopyDst
        });
        unsafe
        {
            fixed (uint* data = mesh.Indices)
                indexBuffer.Write(new ReadOnlySpan<byte>(data, mesh.Indices.Length * sizeof(uint)));
        }

        var buffers = new MeshBuffers { VertexBuffer = vertexBuffer, IndexBuffer = indexBuffer };
        _meshBuffers.Add(mesh, buffers);
        return buffers;
    }

    /// <summary>
    /// Creates (or returns) the per-component group-1 bind group: the model
    /// buffer, the material parameters buffer and the material's textures.
    /// Rebuilt when the component switches shader or technique.
    /// </summary>
    private RenderableResources GetRenderableResources(MeshRenderer renderer, Material material, IPipeline pipeline)
    {
        if (_renderables.TryGetValue(renderer, out var existing) &&
            existing.Shader == material.Shader &&
            existing.Technique == material.Technique)
            return existing;

        if (existing is not null)
            DisposeRenderable(renderer);

        var shader = material.Shader;
        var fields = shader.MaterialFields;
        var materialBuffer = fields.Count == 0
            ? null
            : _device.CreateBuffer(new BufferDescription
            {
                Size = (ulong)UniformPacker.ComputeStructSize(fields),
                Usage = BufferUsage.Uniform | BufferUsage.CopyDst
            });
        var modelBuffer = _device.CreateBuffer(new BufferDescription
        {
            Size = 64,
            Usage = BufferUsage.Uniform | BufferUsage.CopyDst
        });

        var bindings = new List<BindGroupBinding>();
        foreach (var binding in shader.Bindings.Where(b => b.Group == 1).OrderBy(b => b.Slot))
        {
            switch (binding.Kind)
            {
                case ShaderBindingKind.UniformBuffer when binding.TypeName is "mat4x4<f32>" or "mat4f":
                    bindings.Add(new BindGroupBinding { Slot = binding.Slot, Buffer = modelBuffer, BufferSize = 64 });
                    break;
                case ShaderBindingKind.UniformBuffer:
                    if (materialBuffer is null)
                    {
                        throw new InvalidOperationException(
                            $"Shader '{shader.Name}' binds uniform '{binding.VariableName}' ({binding.TypeName}) but declares no material struct.");
                    }
                    bindings.Add(new BindGroupBinding
                    {
                        Slot = binding.Slot,
                        Buffer = materialBuffer,
                        BufferSize = (ulong)materialBuffer.Size
                    });
                    break;
                case ShaderBindingKind.Sampler:
                    bindings.Add(new BindGroupBinding { Slot = binding.Slot, Sampler = _materialSampler });
                    break;
                case ShaderBindingKind.Texture:
                    bindings.Add(new BindGroupBinding
                    {
                        Slot = binding.Slot,
                        Texture = GetMaterialTexture(material, binding.VariableName)
                    });
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Shader '{shader.Name}' declares unsupported material binding '{binding.VariableName}'.");
            }
        }

        var resources = new RenderableResources
        {
            Shader = shader,
            Technique = material.Technique,
            ModelBuffer = modelBuffer,
            MaterialBuffer = materialBuffer,
            BindGroup = pipeline.CreateBindGroup(1, bindings)
        };
        _renderables.Add(renderer, resources);
        return resources;
    }

    /// <summary>
    /// Resolves a material texture slot: the material's own texture (uploaded
    /// and cached once) or a sensible 1x1 default. Color textures (albedo,
    /// emissive) are created in sRGB so the hardware decodes them to linear;
    /// data maps (normal, metallic/roughness, occlusion) stay plain unorm.
    /// </summary>
    private ITexture GetMaterialTexture(Material material, string slotName)
    {
        if (material.Textures.TryGetValue(slotName, out var cpuTexture))
        {
            if (_materialTextures.TryGetValue(cpuTexture, out var existing))
                return existing;

            var srgb = IsColorTextureSlot(slotName);
            var gpu = _device.CreateTexture(new TextureDescription
            {
                Width = cpuTexture.Width,
                Height = cpuTexture.Height,
                Format = srgb ? TextureFormat.Rgba8UnormSrgb : TextureFormat.Rgba8Unorm,
                Sampled = true,
                CopyDestination = true
            });
            unsafe
            {
                fixed (byte* pixels = cpuTexture.Pixels)
                    gpu.Write((nint)pixels, cpuTexture.Width * 4, 0, 0, cpuTexture.Width, cpuTexture.Height);
            }
            _materialTextures.Add(cpuTexture, gpu);
            return gpu;
        }

        // Unbound slots get a flat blue normal map, a black emissive map, and
        // white for albedo/metallic-roughness/occlusion (neutral factors).
        if (slotName.Contains("normal", StringComparison.OrdinalIgnoreCase))
            return _defaultNormalTexture;
        if (slotName.Contains("emissive", StringComparison.OrdinalIgnoreCase))
            return _defaultBlackTexture;
        return _defaultWhiteTexture;
    }

    private static bool IsColorTextureSlot(string slotName) =>
        slotName.Contains("albedo", StringComparison.OrdinalIgnoreCase) ||
        slotName.Contains("emissive", StringComparison.OrdinalIgnoreCase);

    /// <summary>Disposes the GPU state of one renderable and drops its cache entry.</summary>
    private void DisposeRenderable(MeshRenderer renderer)
    {
        if (!_renderables.Remove(renderer, out var resources))
            return;

        resources.BindGroup.Dispose();
        resources.ModelBuffer.Dispose();
        resources.MaterialBuffer?.Dispose();
    }

    private static Matrix4x4 ToWorldMatrix(Transform transform) =>
        Matrix4x4.CreateScale(transform.Scale)
        * Matrix4x4.CreateFromQuaternion(transform.Rotation.Quaternion)
        * Matrix4x4.CreateTranslation(transform.Position);

    private void CreateUiResources()
    {
        _uiSampler ??= _device.CreateSampler(new SamplerDescription());

        string shaderSource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "Ui.wgsl"));
        _uiPipeline ??= _device.CreatePipeline(new PipelineDescription
        {
            ShaderSource = shaderSource,
            VertexEntryPoint = "vs_main",
            FragmentEntryPoint = "fs_main",
            ColorFormat = _device.Swapchain.Format,
            DepthFormat = TextureFormat.Depth24Plus,
            AlphaBlend = true,
            VertexLayout = new VertexBufferLayoutDescription
            {
                Stride = 4 * sizeof(float),
                Attributes =
                [
                    new VertexAttributeDescription { Format = VertexFormat.Float32x2, Offset = 0, ShaderLocation = 0 },
                    new VertexAttributeDescription { Format = VertexFormat.Float32x2, Offset = 2 * sizeof(float), ShaderLocation = 1 }
                ]
            },
            BindGroups =
            [
                [
                    new BindGroupLayoutBinding { Slot = 0, Type = BindingType.Texture, Stages = ShaderStage.Fragment },
                    new BindGroupLayoutBinding { Slot = 1, Type = BindingType.Sampler, Stages = ShaderStage.Fragment }
                ]
            ]
        });

        if (_uiVertexBuffer == null)
        {
            float[] vertices = [-1, -1, 0, 1, 1, -1, 1, 1, 1, 1, 1, 0, 1, 1, 1, 0, -1, 1, 0, 0, -1, -1, 0, 1];
            _uiVertexBuffer = _device.CreateBuffer(new BufferDescription
            {
                Size = (ulong)(vertices.Length * sizeof(float)),
                Usage = BufferUsage.Vertex | BufferUsage.CopyDst
            });
            unsafe
            {
                fixed (float* data = vertices)
                    _uiVertexBuffer.Write(new ReadOnlySpan<byte>(data, vertices.Length * sizeof(float)));
            }
        }
    }

    /// <summary>
    /// Creates the blit pipeline that draws the Renderer2D offscreen target onto
    /// the surface. The bind group is recreated lazily in <see cref="Render"/>
    /// because the Renderer2D's target texture is created on its first frame.
    /// </summary>
    private void CreateUi2DResources()
    {
        string shaderSource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "Ui2D.wgsl"));
        _ui2dSampler ??= _device.CreateSampler(new SamplerDescription());
        _ui2dPipeline ??= _device.CreatePipeline(new PipelineDescription
        {
            ShaderSource = shaderSource,
            VertexEntryPoint = "vs_main",
            FragmentEntryPoint = "fs_main",
            ColorFormat = _device.Swapchain.Format,
            DepthFormat = TextureFormat.Depth24Plus,
            AlphaBlend = true,
            VertexLayout = new VertexBufferLayoutDescription
            {
                Stride = 4 * sizeof(float),
                Attributes =
                [
                    new VertexAttributeDescription { Format = VertexFormat.Float32x2, Offset = 0, ShaderLocation = 0 },
                    new VertexAttributeDescription { Format = VertexFormat.Float32x2, Offset = 2 * sizeof(float), ShaderLocation = 1 }
                ]
            },
            BindGroups =
            [
                [
                    new BindGroupLayoutBinding { Slot = 0, Type = BindingType.Texture, Stages = ShaderStage.Fragment },
                    new BindGroupLayoutBinding { Slot = 1, Type = BindingType.Sampler, Stages = ShaderStage.Fragment }
                ]
            ]
        });
    }

    private void CreateBackdropResources()
    {
        string shaderSource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "Backdrop.wgsl"));
        _backdropParamsBuffer = _device.CreateBuffer(new BufferDescription
        {
            Size = (ulong)(MaxBackdropRegions * sizeof(BackdropGpuParams)),
            Usage = BufferUsage.Storage | BufferUsage.CopyDst
        });
        _backdropParamsBytes = null;

        _backdropPipeline = _device.CreatePipeline(new PipelineDescription
        {
            ShaderSource = shaderSource,
            VertexEntryPoint = "vs_main",
            FragmentEntryPoint = "fs_main",
            ColorFormat = _device.Swapchain.Format,
            DepthFormat = TextureFormat.Depth24Plus,
            AlphaBlend = true,
            VertexLayout = new VertexBufferLayoutDescription
            {
                Stride = 4 * sizeof(float),
                Attributes =
                [
                    new VertexAttributeDescription { Format = VertexFormat.Float32x2, Offset = 0, ShaderLocation = 0 },
                    new VertexAttributeDescription { Format = VertexFormat.Float32x2, Offset = 2 * sizeof(float), ShaderLocation = 1 }
                ]
            },
            BindGroups =
            [
                [
                    new BindGroupLayoutBinding { Slot = 0, Type = BindingType.Texture, Stages = ShaderStage.Fragment },
                    new BindGroupLayoutBinding { Slot = 1, Type = BindingType.Sampler, Stages = ShaderStage.Fragment },
                    new BindGroupLayoutBinding { Slot = 2, Type = BindingType.ReadOnlyStorageBuffer, Stages = ShaderStage.Fragment }
                ]
            ]
        });
    }

    private void CreateDepthTexture(int width, int height)
    {
        _depthTexture?.Dispose();
        _depthTexture = _device.CreateTexture(new TextureDescription
        {
            Width = Math.Max(1, width),
            Height = Math.Max(1, height),
            Format = TextureFormat.Depth24Plus,
            RenderTarget = true
        });
    }

    /// <summary>
    /// Creates the offscreen scene texture and the two bind groups that sample
    /// it: the blit (scene onto the surface, reusing the UI pipeline layout)
    /// and the backdrop compositor (scene + sampler + per-region params).
    /// </summary>
    private void CreateSceneResources(int width, int height)
    {
        _sceneBindGroup?.Dispose();
        _backdropBindGroup?.Dispose();
        _sceneTexture?.Dispose();

        width = Math.Max(1, width);
        height = Math.Max(1, height);

        _sceneTexture = _device.CreateTexture(new TextureDescription
        {
            Width = width,
            Height = height,
            Format = _device.Swapchain.Format,
            RenderTarget = true,
            Sampled = true
        });

        _sceneBindGroup = _uiPipeline.CreateBindGroup(
        [
            new BindGroupBinding { Slot = 0, Texture = _sceneTexture },
            new BindGroupBinding { Slot = 1, Sampler = _uiSampler }
        ]);

        _backdropBindGroup = _backdropPipeline.CreateBindGroup(
        [
            new BindGroupBinding { Slot = 0, Texture = _sceneTexture },
            new BindGroupBinding { Slot = 1, Sampler = _uiSampler },
            new BindGroupBinding { Slot = 2, Buffer = _backdropParamsBuffer, BufferSize = (ulong)(MaxBackdropRegions * sizeof(BackdropGpuParams)) }
        ]);
    }

    /// <summary>
    /// Creates the selection-outline resources: the mask render target and the
    /// two passes (mask render + dilated composite). Rebuilt on resize like the
    /// other viewport-sized resources; pipelines and buffers are created once.
    /// </summary>
    private void CreateSelectionOutlineResources(int width, int height)
    {
        _outlineBindGroup?.Dispose();
        _selectionMaskSceneBindGroup?.Dispose();
        _selectionMaskModelBindGroup?.Dispose();
        _selectionTexture?.Dispose();

        width = Math.Max(1, width);
        height = Math.Max(1, height);

        _selectionTexture = _device.CreateTexture(new TextureDescription
        {
            Width = width,
            Height = height,
            // Plain unorm: the mask is a binary silhouette in the red channel;
            // sampling it bilinear slightly softens the edge, smoothing the
            // outline band.
            Format = TextureFormat.Rgba8Unorm,
            RenderTarget = true,
            Sampled = true
        });

        _selectionMaskModelBuffer ??= _device.CreateBuffer(new BufferDescription
        {
            Size = 64,
            Usage = BufferUsage.Uniform | BufferUsage.CopyDst
        });
        _outlineParamsBuffer ??= _device.CreateBuffer(new BufferDescription
        {
            Size = (ulong)sizeof(SelectionOutlineParams),
            Usage = BufferUsage.Uniform | BufferUsage.CopyDst
        });

        // The mask pipeline reuses the mesh vertex buffers (48-byte stride,
        // position at location 0) and the shared scene buffer; only the model
        // uniform is per-renderable.
        _selectionMaskPipeline ??= _device.CreatePipeline(new PipelineDescription
        {
            ShaderSource = Shader.Load(Path.Combine("Shaders", "SelectionMask.wgsl")).Source,
            VertexEntryPoint = "vs_main",
            FragmentEntryPoint = "fs_main",
            ColorFormat = TextureFormat.Rgba8Unorm,
            DepthFormat = TextureFormat.Depth24Plus,
            // Test against the scene depth (loaded, not cleared) so the mask
            // covers exactly the visible part of the selection.
            DepthWriteEnabled = false,
            DepthCompare = CompareFunction.LessEqual,
            VertexLayout = new VertexBufferLayoutDescription
            {
                Stride = 12 * sizeof(float),
                Attributes =
                [
                    new VertexAttributeDescription { Format = VertexFormat.Float32x3, Offset = 0, ShaderLocation = 0 }
                ]
            },
            BindGroups =
            [
                [
                    new BindGroupLayoutBinding { Slot = 0, Type = BindingType.UniformBuffer, Stages = ShaderStage.Vertex }
                ],
                [
                    new BindGroupLayoutBinding { Slot = 0, Type = BindingType.UniformBuffer, Stages = ShaderStage.Vertex }
                ]
            ]
        });

        _outlinePipeline ??= _device.CreatePipeline(new PipelineDescription
        {
            ShaderSource = Shader.Load(Path.Combine("Shaders", "SelectionOutline.wgsl")).Source,
            VertexEntryPoint = "vs_main",
            FragmentEntryPoint = "fs_main",
            ColorFormat = _device.Swapchain.Format,
            DepthFormat = TextureFormat.Depth24Plus,
            AlphaBlend = true,
            VertexLayout = new VertexBufferLayoutDescription
            {
                Stride = 4 * sizeof(float),
                Attributes =
                [
                    new VertexAttributeDescription { Format = VertexFormat.Float32x2, Offset = 0, ShaderLocation = 0 },
                    new VertexAttributeDescription { Format = VertexFormat.Float32x2, Offset = 2 * sizeof(float), ShaderLocation = 1 }
                ]
            },
            BindGroups =
            [
                [
                    new BindGroupLayoutBinding { Slot = 0, Type = BindingType.Texture, Stages = ShaderStage.Fragment },
                    new BindGroupLayoutBinding { Slot = 1, Type = BindingType.Texture, Stages = ShaderStage.Fragment },
                    new BindGroupLayoutBinding { Slot = 2, Type = BindingType.Sampler, Stages = ShaderStage.Fragment },
                    new BindGroupLayoutBinding { Slot = 3, Type = BindingType.UniformBuffer, Stages = ShaderStage.Fragment }
                ]
            ]
        });

        _selectionMaskSceneBindGroup = _selectionMaskPipeline.CreateBindGroup(0,
        [
            new BindGroupBinding { Slot = 0, Buffer = _sceneBuffer, BufferSize = (ulong)sizeof(SceneUniforms) }
        ]);
        _selectionMaskModelBindGroup = _selectionMaskPipeline.CreateBindGroup(1,
        [
            new BindGroupBinding { Slot = 0, Buffer = _selectionMaskModelBuffer, BufferSize = 64 }
        ]);
        _outlineBindGroup = _outlinePipeline.CreateBindGroup(
        [
            new BindGroupBinding { Slot = 0, Texture = _sceneTexture },
            new BindGroupBinding { Slot = 1, Texture = _selectionTexture },
            new BindGroupBinding { Slot = 2, Sampler = _uiSampler },
            new BindGroupBinding { Slot = 3, Buffer = _outlineParamsBuffer, BufferSize = (ulong)sizeof(SelectionOutlineParams) }
        ]);
    }

    /// <summary>True while the outline should draw: enabled and a mesh-shaped selection exists.</summary>
    private bool IsSelectionOutlineActive() =>
        Outline.Enabled && Gizmos.Selection?.GetComponent<MeshRenderer>() is { IsValid: true, Model: not null };

    /// <summary>Draws the selected mesh into the selection mask texture.</summary>
    private void DrawSelectionMask(IRenderPass pass)
    {
        var renderer = Gizmos.Selection?.GetComponent<MeshRenderer>();
        if (renderer is null || !renderer.IsValid || renderer.Model is null)
            return;

        pass.SetPipeline(_selectionMaskPipeline);
        pass.SetBindGroup(_selectionMaskSceneBindGroup, 0);
        pass.SetBindGroup(_selectionMaskModelBindGroup, 1);

        var modelMatrix = ToWorldMatrix(renderer.World);
        _selectionMaskModelBuffer.Write(in modelMatrix);

        foreach (var mesh in renderer.Model.Meshes)
        {
            var buffers = GetMeshBuffers(mesh);
            pass.SetVertexBuffer(buffers.VertexBuffer, buffers.VertexBuffer.Size);
            pass.SetIndexBuffer(buffers.IndexBuffer, buffers.IndexBuffer.Size);
            pass.DrawIndexed((uint)mesh.Indices.Length);
        }
    }

    /// <summary>Writes the outline color/size into the composite pass uniform.</summary>
    private void UpdateOutlineParams()
    {
        var parameters = new SelectionOutlineParams
        {
            Color = new Vector4(Outline.Color, Outline.Opacity),
            TexelSize = new Vector2(1f / _width, 1f / _height),
            Thickness = Outline.Thickness
        };
        _outlineParamsBuffer.Write(in parameters);
    }

    /// <summary>
    /// Translates the backdrop regions collected by the tree-walk painter into
    /// the GPU parameter buffer (Backdrop.wgsl reads one struct per instance)
    /// and uploads it only when the set actually changed, so a static UI costs
    /// no uploads at all. The regions already carry the CSS chain in a form the
    /// shader can evaluate (see
    /// <see cref="Crowbar.UI.CssFilterFunctions.IsGpuBackdropExpressible"/>).
    /// </summary>
    private void UpdateBackdropParams(IReadOnlyList<BackdropRegion> regions)
    {
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
        _backdropParamsBuffer.Write(bytes);
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

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _sceneBindGroup?.Dispose();
        _sceneTexture?.Dispose();
        _backdropBindGroup?.Dispose();
        _backdropParamsBuffer?.Dispose();
        _backdropPipeline?.Dispose();
        _uiVertexBuffer?.Dispose();
        _uiPipeline?.Dispose();
        _uiSampler?.Dispose();
        _ui2dBindGroup?.Dispose();
        _ui2dPipeline?.Dispose();
        _ui2dSampler?.Dispose();
        _ui2d?.Dispose();
        foreach (var resources in _renderables.Values)
        {
            resources.BindGroup.Dispose();
            resources.ModelBuffer.Dispose();
            resources.MaterialBuffer?.Dispose();
        }
        _renderables.Clear();
        foreach (var buffers in _meshBuffers.Values)
        {
            buffers.VertexBuffer.Dispose();
            buffers.IndexBuffer.Dispose();
        }
        _meshBuffers.Clear();
        foreach (var bindGroup in _sceneBindGroups.Values)
            bindGroup.Dispose();
        _sceneBindGroups.Clear();
        foreach (var pipeline in _meshPipelines.Values)
            pipeline.Dispose();
        _meshPipelines.Clear();
        foreach (var texture in _materialTextures.Values)
            texture.Dispose();
        _materialTextures.Clear();
        _defaultWhiteTexture?.Dispose();
        _defaultBlackTexture?.Dispose();
        _defaultNormalTexture?.Dispose();
        _materialSampler?.Dispose();
        _lightsBuffer?.Dispose();
        _sceneBuffer?.Dispose();
        _gridBindGroup?.Dispose();
        _gridUniformBuffer?.Dispose();
        _gridVertexBuffer?.Dispose();
        _gridPipeline?.Dispose();
        Gizmos?.Dispose();
        _outlineBindGroup?.Dispose();
        _outlineParamsBuffer?.Dispose();
        _outlinePipeline?.Dispose();
        _selectionMaskSceneBindGroup?.Dispose();
        _selectionMaskModelBindGroup?.Dispose();
        _selectionMaskModelBuffer?.Dispose();
        _selectionMaskPipeline?.Dispose();
        _selectionTexture?.Dispose();
        _depthTexture?.Dispose();
    }
}
