using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using Crowbar.Engine;
using Crowbar.Engine.Rendering2D;
using Crowbar.FileSystems;
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
    // Mirrors CameraUniforms in Shaders/Common/Camera.slang: view, projection,
    // camera position and the clock. Written once per frame, shared by every
    // mesh pipeline through bind group 0.
    [StructLayout(LayoutKind.Sequential)]
    private struct CameraUniforms
    {
        public Matrix4x4 View;
        public Matrix4x4 Projection;
        public Vector4 CameraPosition;
        public Vector4 Time;
    }

    // Mirrors LightData in Shaders/Common/Lighting.slang.
    [StructLayout(LayoutKind.Sequential)]
    private struct LightGpuData
    {
        public Vector4 PositionType;     // xyz = position, w = 0 directional / 1 point
        public Vector4 ColorIntensity;   // rgb = color, w = intensity
        public Vector4 DirectionRange;   // xyz = direction the light travels, w = range (point lights)
    }

    // Mirrors LightsUniform in Shaders/Common/Lighting.slang: a u32 count
    // padded to 16 bytes, then array<LightData, 8> (48 bytes per element).
    private const int LightsBufferSize = 16 + MaxLights * 48;

    // Mirrors ShadowFace in Shaders/Common/Shadows.slang: a UV rect (16 bytes)
    // followed by the face's light view-projection matrix (64 bytes).
    [StructLayout(LayoutKind.Sequential)]
    private struct ShadowFaceGpuData
    {
        public Vector4 UvRect;      // u0, v0, u1, v1 in atlas UV space
        public Matrix4x4 ViewProj;  // world -> light clip space
    }

    // Mirrors ShadowLight in Shaders/Common/Shadows.slang: a flags vector plus
    // six face slots (80 bytes each), 496 bytes per light, 16-byte aligned.
    [StructLayout(LayoutKind.Sequential)]
    private struct ShadowLightGpuData
    {
        public Vector4 Flags;       // x = enabled, y = type (0 directional / 1 point), z = face count, w = bias
        public ShadowFaceGpuData Face0;
        public ShadowFaceGpuData Face1;
        public ShadowFaceGpuData Face2;
        public ShadowFaceGpuData Face3;
        public ShadowFaceGpuData Face4;
        public ShadowFaceGpuData Face5;
    }

    // Mirrors ShadowUniforms in Shaders/Common/Shadows.slang: a leading vec4
    // (atlas size) then array<ShadowLight, 8> (496 bytes per element).
    private const int ShadowLightStride = 16 + 6 * 80;
    private const int ShadowBufferSize = 16 + MaxLights * ShadowLightStride;

    // The shadow atlas: a single 2D depth texture (Depth32Float, filterable for
    // PCF) partitioned into 512px tiles. A directional light occupies a 2x2
    // block of tiles (1024px, so its texels stay small enough that the constant
    // bias comfortably clears the per-texel depth gradient); a point light
    // occupies six single tiles (one per cube face).
    private const int ShadowAtlasSize = 2048;
    private const int ShadowTileSize = 512;
    private const int DirectionalShadowTileSize = ShadowTileSize * 2;
    private const int ShadowTilesPerRow = ShadowAtlasSize / ShadowTileSize;
    private const int TotalShadowTiles = ShadowTilesPerRow * ShadowTilesPerRow;
    private const float DirectionalShadowBias = 0.002f;
    private const float PointShadowBias = 0.02f;

    // Multiplier on (1 - |dot(surface normal, light direction)|) added to the
    // per-light bias. Grazing surfaces change depth fastest in the shadow map,
    // so they need proportionally more bias; the value is sent to the shaders
    // in the shadow uniforms (atlasSize.z).
    private const float ShadowSlopeScale = 0.004f;

    // Directional shadows are fit to the camera frustum, but the camera's far
    // plane (100 units) would waste most of the atlas on empty space and make
    // every texel huge. The fit is clamped to this distance so a small scene
    // keeps crisp shadows; geometry beyond it simply stops casting into view.
    private const float DirectionalShadowMaxDistance = 50f;

    // Cube-face orientations for a point light, in the order Shadows.slang
    // selects them: +X, -X, +Y, -Y, +Z, -Z.
    private static readonly (Vector3 Forward, Vector3 Up)[] PointFaceOrientations =
    [
        (Vector3.UnitX, Vector3.UnitY),
        (-Vector3.UnitX, Vector3.UnitY),
        (Vector3.UnitY, -Vector3.UnitZ),
        (-Vector3.UnitY, Vector3.UnitZ),
        (Vector3.UnitZ, Vector3.UnitY),
        (-Vector3.UnitZ, Vector3.UnitY)
    ];

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
        public required int MaterialRevision { get; init; }
        public required IBuffer ModelBuffer { get; init; }
        public IBuffer? MaterialBuffer { get; init; }
        public required IBindGroup BindGroup { get; init; }
    }

    /// <summary>Per-renderable GPU state for the shadow depth pass (model matrix only).</summary>
    private sealed class ShadowRenderable
    {
        public required IBuffer ModelBuffer { get; init; }
        public required IBindGroup BindGroup { get; init; }
    }

    /// <summary>Pipeline identity: shader, technique and the material's rasterizer state.</summary>
    private readonly record struct MeshPipelineKey(
        Shader Shader,
        string Technique,
        MaterialBlendMode BlendMode,
        bool DoubleSided);

    /// <summary>One mesh instance to draw this frame, pre-sorted into render order.</summary>
    private readonly record struct MeshDrawItem(
        MeshRenderer Renderer,
        Mesh Mesh,
        Material Material,
        ModelNode Node,
        IPipeline Pipeline,
        Matrix4x4 ModelMatrix,
        float Depth);

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

    // Mirrors OutlineParams in Shaders/Editor/SelectionOutline.slang.
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
    // camera buffer each frame.
    private CameraUniforms _cameraUniforms;

    // Mesh scene pass. Group 0 holds the per-frame camera + lights buffers;
    // group 1 is per-renderable (model, material, textures) and its layout is
    // derived from the shader's own bindings, so adding a binding to a WGSL
    // file requires no C# change. Pipelines are cached per (shader, technique),
    // GPU geometry per Mesh, renderable state per component.
    private const int MaxLights = 8;
    private static readonly BindGroupLayoutBinding[] FrameGroupBindings =
    [
        new() { Slot = 0, Type = BindingType.UniformBuffer, Stages = ShaderStage.Vertex | ShaderStage.Fragment },
        new() { Slot = 1, Type = BindingType.UniformBuffer, Stages = ShaderStage.Fragment },
        new() { Slot = 2, Type = BindingType.UniformBuffer, Stages = ShaderStage.Fragment },
        new() { Slot = 3, Type = BindingType.DepthTexture, Stages = ShaderStage.Fragment },
        new() { Slot = 4, Type = BindingType.ComparisonSampler, Stages = ShaderStage.Fragment }
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
    private IBuffer _cameraBuffer = null!;
    private IBuffer _lightsBuffer = null!;
    private ISampler _materialSampler = null!;
    private ITexture _defaultWhiteTexture = null!;
    private ITexture _defaultBlackTexture = null!;
    private ITexture _defaultNormalTexture = null!;
    private readonly Dictionary<MeshPipelineKey, IPipeline> _meshPipelines = [];
    private readonly Dictionary<IPipeline, IBindGroup> _cameraBindGroups = [];
    private readonly Dictionary<IPipeline, IBindGroup> _environmentBindGroups = [];
    private readonly Dictionary<Mesh, MeshBuffers> _meshBuffers = [];
    private readonly Dictionary<(MeshRenderer Renderer, Material Material, ModelNode Node), RenderableResources> _renderables = [];
    private readonly Dictionary<Texture2D, ITexture> _materialTextures = [];
    private Material? _defaultMaterial;
    private ITexture _defaultEnvironmentCube = null!;
    private ITexture _defaultIrradianceCube = null!;
    private ITexture _defaultPrefilteredCube = null!;
    private ITexture _defaultBrdfLut = null!;
    private ISampler _environmentSampler = null!;
    private IBuffer _environmentUniformBuffer = null!;
    private SceneEnvironment? _boundEnvironment;
    private ITexture? _boundEnvironmentMap;
    private readonly EnvironmentPreprocessor _environmentPreprocessor;
    private IPipeline _skyPipeline = null!;
    private IBindGroup _skyCameraBindGroup = null!;
    private IBindGroup _skyPlaceholderBindGroup = null!;

    // Mirrors EnvironmentUniforms in Shaders/Common/Environment.slang:
    // rotation/intensity/exposure/max mip, tint, provider flag, then the sun
    // (direction + angular radius) and atmosphere (turbidity, albedo, sun
    // intensity) parameters used by the procedural sky.
    [StructLayout(LayoutKind.Sequential)]
    private struct EnvironmentUniforms
    {
        public Vector4 Parameters;
        public Vector4 Tint;
        public Vector4 Provider;
        public Vector4 Sun;
        public Vector4 Atmosphere;
    }

    /// <summary>Fallback sun direction when the scene has no directional light.</summary>
    private static readonly Vector3 DefaultSunDirection = Vector3.Normalize(new Vector3(-0.35f, 0.8f, -0.2f));

    // Reused across frames to collect the meshes/textures/nodes still referenced
    // by the world, so GPU resources whose CPU owner is gone can be released.
    private readonly HashSet<Mesh> _liveMeshes = [];
    private readonly HashSet<Texture2D> _liveTextures = [];
    private readonly HashSet<ModelNode> _liveNodes = [];

    // Shadow mapping: the depth atlas, its comparison sampler, the per-light
    // shadow metadata buffer and the depth-only pipeline that renders each
    // face into a tile. Each tile owns its own light view-projection buffer
    // and bind group: QueueWriteBuffer uploads are ordered before the command
    // buffer runs, so a single shared buffer would end up holding the last
    // face's matrix for every face. Per-renderable model buffers are shared
    // with the scene pass.
    private ITexture _shadowAtlas = null!;
    private ISampler _shadowSampler = null!;
    private IBuffer _shadowDataBuffer = null!;
    private IPipeline _shadowPipeline = null!;
    private IBuffer[] _shadowViewProjBuffers = null!;
    private IBindGroup[] _shadowViewProjBindGroups = null!;
    private readonly Dictionary<(MeshRenderer Renderer, ModelNode Node), ShadowRenderable> _shadowRenderables = [];

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
    private readonly Dictionary<ModelNode, IBindGroup> _selectionMaskModelBindGroups = [];
    private readonly Dictionary<ModelNode, IBuffer> _selectionMaskModelBuffers = [];
    private IPipeline _outlinePipeline = null!;
    private IBindGroup _outlineBindGroup = null!;
    private IBuffer _outlineParamsBuffer = null!;

    // Offscreen 3D scene: the cube renders here instead of directly on the
    // surface, then the scene is blitted to the surface. backdrop-filter
    // panels are composited on the GPU by Ui/Backdrop.slang sampling this texture
    // directly (like S&box's ui_backdropfilter.shader), so the CPU never sees
    // the scene and the UI layer only re-records when the UI changes.
    private ITexture _sceneTexture = null!;
    private IBindGroup _sceneBindGroup = null!;

    // Post-process chain: the enabled PostProcess components run in order,
    // ping-ponging the linear HDR scene through HDR intermediates, and the
    // last pass writes this display-referred texture, which the surface
    // blit, backdrop and outline bind groups sample. Pipelines are cached per
    // shader path (recreated on hot reload); per-frame uniform buffers and
    // bind groups are tracked for disposal after submit; the scratch pool
    // serves multi-pass effects with internal ping-pong.
    private ITexture _displayTexture = null!;
    private ITexture _postProcessTextureA = null!;
    private ITexture _postProcessTextureB = null!;
    private readonly List<IDisposable> _postProcessFrameResources = [];
    private readonly Dictionary<string, IPipeline> _postProcessPipelines = [];
    private ITexture[] _postProcessScratch = [];
    private ISampler _postProcessPointSampler = null!;
    // Components already announced once in the console (per session).
    private readonly HashSet<PostProcess> _loggedPostProcessDrivers = [];
    private bool _loggedIdentityCopy;

    /// <summary>
    /// The engine-neutral identity pass: copies the scene to the display
    /// unchanged when no PostProcess component exists (or the camera disabled
    /// post-processing). The engine never applies a tonemapping on its own —
    /// the default look is level content (the demo level ships a Tonemapping
    /// component on its camera).
    /// </summary>
    private const string CopyShaderPath = "Shaders/PostProcesses/Copy.wgsl";

    // The 3D scene renders into viewport-sized targets (its color, depth and
    // selection mask all share the viewport dimensions). The surface composite
    // pass still needs a full-window depth attachment because its color target
    // is the swapchain texture.
    private ITexture _sceneDepth = null!;
    private ITexture _surfaceDepth = null!;

    // Scene blit: the UI pipeline (Ui/Blit.slang) also blits the offscreen scene
    // texture onto the surface, so it is kept even though the Skia UI texture
    // upload path is gone.
    private ISampler _uiSampler = null!;
    // Scene blit pipeline: presents the linear post-processed display texture
    // to the (non-sRGB) surface, applying the single linear->sRGB encode.
    private IPipeline _scenePipeline = null!;
    private IBuffer _uiVertexBuffer = null!;
    // Fullscreen quad transformed to the viewport rectangle in NDC, used by the
    // scene blit and the selection-outline composite. The backdrop compositor
    // and the UI overlay keep the plain fullscreen quad.
    private IBuffer _sceneQuadVertexBuffer = null!;
    private int _sceneTargetWidth;
    private int _sceneTargetHeight;
    private UiRect _sceneQuadRect;
    private int _sceneQuadWindowWidth;
    private int _sceneQuadWindowHeight;

    // GPU UI renderer: the tree-walk painter records the panel tree into a
    // Renderer2D, which draws the UI offscreen on the GPU (no Skia raster).
    // The surface pass blits that target over the scene with Ui/BlitLinear.slang.
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
        _environmentPreprocessor = new EnvironmentPreprocessor(_device);
        CreateShadowResources();
        CreateGridResources();
        Gizmos = new GizmoRenderer(_device, _cameraBuffer, (ulong)sizeof(CameraUniforms));
        CreateBackdropResources();
        CreateUiResources();
        CreateSurfaceDepth(_width, _height);
        EnsureSceneTargets(SceneViewport);
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
        EnsureSceneTargets(viewport);

        // Collect the world's enabled lights once; the shadow pass and the
        // scene pass must agree on each light's index in the shared buffers.
        var lights = CollectLights(world);
        UpdateEnvironment(world);

        ITexture? frame = _device.Swapchain.AcquireTexture();
        if (frame is null)
            return;

        using (frame)
        {
            using ICommandBuffer commandBuffer = _device.CreateCommandBuffer();
            _environmentPreprocessor.Update(world?.Environment, commandBuffer);

            // Pass 0: render the shadow-casting lights into the depth atlas, so
            // the scene pass can sample it (the atlas is written in one pass and
            // read in a later pass, which WebGPU barriers allow).
            UpdateShadows(commandBuffer, world, lights);

            // Pass 1: render the 3D scene into the offscreen scene texture (it is
            // both blitted to the surface and copied back for the UI backdrop).
            // The scene targets are sized to the viewport, so the whole texture
            // is the viewport — no scissor is needed.
            var scenePassDescription = new RenderPassDescription
            {
                Color = new ColorAttachment
                {
                    Texture = _sceneTexture,
                    LoadOp = RenderAttachmentLoadOp.Clear,
                    StoreOp = RenderAttachmentStoreOp.Store,
                    ClearColor = new Vector4(0f, 0f, 0f, 1f)
                },
                Depth = new DepthAttachment
                {
                    Texture = _sceneDepth,
                    LoadOp = RenderAttachmentLoadOp.Clear,
                    StoreOp = RenderAttachmentStoreOp.Store,
                    ClearValue = 1f
                }
            };
            using (IRenderPass scenePass = commandBuffer.BeginRenderPass(scenePassDescription))
            {
                DrawSky(scenePass);
                DrawMeshRenderers(scenePass, world, time, lights);
                DrawGrid(scenePass);
                Gizmos.Draw(scenePass, world, camera, _sceneTargetWidth, _sceneTargetHeight);
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
                        Texture = _sceneDepth,
                        LoadOp = RenderAttachmentLoadOp.Load,
                        StoreOp = RenderAttachmentStoreOp.Store
                    }
                }))
                {
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

            // Pass 1.5: the post-process chain. The scene texture is linear
            // HDR; each enabled PostProcess component runs in Order, writing
            // the next chain target (ping-ponging through HDR intermediates),
            // and the last pass writes the display texture. With no component
            // (or when the camera disabled post-processing) the scene is
            // copied to the display as-is — the engine applies no tonemapping
            // by default; that look is level content, e.g. the demo level's
            // Tonemapping component on its camera.
            var postProcessGroups = BuildPostProcessGroups(world, camera.Position);
            if (!camera.EnablePostProcessing || postProcessGroups.Count == 0)
            {
                if (!_loggedIdentityCopy)
                {
                    _loggedIdentityCopy = true;
                    Log.Info(camera.EnablePostProcessing
                        ? "[PostProcess] No components — presenting the scene as-is (no default tonemapping)"
                        : "[PostProcess] Camera post-processing disabled — presenting the scene as-is");
                }
                RunPostProcessPass(commandBuffer, _sceneTexture, _displayTexture,
                    CopyShaderPath, null, PostProcessSampler.Linear);
            }
            else
            {
                var postProcessInput = _sceneTexture;
                for (var index = 0; index < postProcessGroups.Count; index++)
                {
                    var group = postProcessGroups[index];
                    var isLast = index == postProcessGroups.Count - 1;
                    var output = isLast
                        ? _displayTexture
                        : (index % 2 == 0 ? _postProcessTextureA : _postProcessTextureB);
                    var driver = group.Driver;
                    // One diagnostic line per effect instance that actually
                    // drives a pass: confirms in the console which components
                    // the chain sees.
                    if (_loggedPostProcessDrivers.Add(driver))
                        Log.Info($"[PostProcess] Applying component {driver.GetType().Name} (Order {driver.Order}, {group.Entries.Count} instance(s))");
                    var context = new PostProcessContext(this, commandBuffer, postProcessInput, output,
                        _sceneDepth, driver.Sampler, group.Entries);
                    PostProcessContext.Current = context;
                    try
                    {
                        driver.Render(context);
                    }
                    finally
                    {
                        PostProcessContext.Current = null;
                    }
                    postProcessInput = output;
                }
            }

            // Pass 2: composite the scene, the backdrop-filter regions and the UI
            // onto the surface. The scene blit reuses the UI pipeline (opaque
            // texture, so the alpha blend is a plain overwrite); the backdrop
            // compositor samples the display texture on the GPU between the blit
            // and the UI overlay.
            var surfacePassDescription = new RenderPassDescription
            {
                Color = new ColorAttachment
                {
                    Texture = frame,
                    LoadOp = RenderAttachmentLoadOp.Clear,
                    StoreOp = RenderAttachmentStoreOp.Store,
                    ClearColor = new Vector4(0f, 0f, 0f, 1f)
                },
                Depth = new DepthAttachment
                {
                    Texture = _surfaceDepth,
                    LoadOp = RenderAttachmentLoadOp.Clear,
                    StoreOp = RenderAttachmentStoreOp.Store,
                    ClearValue = 1f
                }
            };
            using (IRenderPass surfacePass = commandBuffer.BeginRenderPass(surfacePassDescription))
            {
                // The scene blit, the backdrop compositor and the UI overlay
                // each bind their own pipeline.
                // wgpu-native's SetPipeline is comparatively expensive (global lock
                // + validation), so binding the same pipeline twice per frame is
                // avoided: the command stream keeps the last bound pipeline until
                // it changes.
                IPipeline currentPipeline = _scenePipeline;
                if (outlineActive)
                {
                    // The selection outline pass replaces the plain scene blit.
                    // Both draw the viewport-rect quad (not the fullscreen one).
                    UpdateOutlineParams();
                    surfacePass.SetPipeline(_outlinePipeline);
                    currentPipeline = _outlinePipeline;
                    surfacePass.SetBindGroup(_outlineBindGroup, 0);
                    surfacePass.SetVertexBuffer(_sceneQuadVertexBuffer, 6 * 4 * sizeof(float));
                    surfacePass.Draw(6);
                }
                else
                {
                    surfacePass.SetPipeline(_scenePipeline);
                    surfacePass.SetBindGroup(_sceneBindGroup, 0);
                    surfacePass.SetVertexBuffer(_sceneQuadVertexBuffer, 6 * 4 * sizeof(float));
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
            _environmentPreprocessor.FinishFrame();
            DisposePostProcessFrameResources();
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
        CreateSurfaceDepth(_width, _height);
        EnsureSceneTargets(SceneViewport);
    }

    private void UpdateCamera(Camera camera)
    {
        var viewport = SceneViewport;
        var matrices = CameraMatrices.Compute(camera, Math.Max(1, (int)viewport.Width), Math.Max(1, (int)viewport.Height));
        _cameraUniforms = new CameraUniforms
        {
            View = matrices.View,
            Projection = matrices.Projection,
            CameraPosition = new Vector4(camera.Position, 1f),
            Time = new Vector4(0f, 0f, 0f, 0f)
        };
    }

    /// <summary>Clamps the host viewport to the framebuffer so its targets never exceed the window.</summary>
    private UiRect ClampViewport(UiRect viewport)
    {
        var x = Math.Max(0, viewport.X);
        var y = Math.Max(0, viewport.Y);
        var right = Math.Min(_width, viewport.X + Math.Max(1, viewport.Width));
        var bottom = Math.Min(_height, viewport.Y + Math.Max(1, viewport.Height));
        return new UiRect(x, y, Math.Max(1, right - x), Math.Max(1, bottom - y));
    }

    /// <summary>
    /// Recreates the viewport-sized scene targets (scene color, scene depth,
    /// selection mask) and the viewport-rect blit quad whenever the viewport
    /// size or position changes. A dock drag therefore only reallocates these
    /// small textures, never the full-window ones.
    /// </summary>
    private void EnsureSceneTargets(UiRect viewport)
    {
        var rect = ClampViewport(viewport);
        var width = (int)rect.Width;
        var height = (int)rect.Height;

        if (width != _sceneTargetWidth || height != _sceneTargetHeight)
        {
            _sceneTargetWidth = width;
            _sceneTargetHeight = height;
            CreateSceneDepth(width, height);
            CreateSceneResources(width, height);
            CreateSelectionOutlineResources(width, height);
            _sceneQuadRect = default;
        }

        UpdateSceneQuad(rect);
    }

    private void UpdateSceneQuad(UiRect rect)
    {
        if (_sceneQuadRect == rect && _sceneQuadWindowWidth == _width && _sceneQuadWindowHeight == _height)
            return;
        _sceneQuadRect = rect;
        _sceneQuadWindowWidth = _width;
        _sceneQuadWindowHeight = _height;

        _sceneQuadVertexBuffer ??= _device.CreateBuffer(new BufferDescription
        {
            Size = 6 * 4 * sizeof(float),
            Usage = BufferUsage.Vertex | BufferUsage.CopyDst
        });

        // The fullscreen quad's winding/UV order, but clipped to the viewport
        // rectangle in NDC (bottom-left, bottom-right, top-right, top-left).
        var x0 = rect.X / _width * 2f - 1f;
        var x1 = (rect.X + rect.Width) / _width * 2f - 1f;
        var y0 = 1f - (rect.Y + rect.Height) / _height * 2f;
        var y1 = 1f - rect.Y / _height * 2f;
        float[] vertices =
        [
            x0, y0, 0, 1,  x1, y0, 1, 1,  x1, y1, 1, 0,
            x1, y1, 1, 0,  x0, y1, 0, 0,  x0, y0, 0, 1
        ];
        unsafe
        {
            fixed (float* data = vertices)
                _sceneQuadVertexBuffer.Write(new ReadOnlySpan<byte>(data, vertices.Length * sizeof(float)));
        }
    }

    private void CreateMeshResources()
    {
        _cameraBuffer = _device.CreateBuffer(new BufferDescription
        {
            Size = (ulong)sizeof(CameraUniforms),
            Usage = BufferUsage.Uniform | BufferUsage.CopyDst
        });
        _lightsBuffer = _device.CreateBuffer(new BufferDescription
        {
            Size = (ulong)LightsBufferSize,
            Usage = BufferUsage.Uniform | BufferUsage.CopyDst
        });
        _materialSampler = _device.CreateSampler(new SamplerDescription
        {
            AddressMode = SamplerAddressMode.Repeat,
            // Trilinear (linear across mip levels) + anisotropic to keep model
            // textures crisp at distance and at grazing angles.
            MipmapFilter = SamplerFilter.Linear,
            MaxAnisotropy = 16
        });

        // 1x1 fallbacks for texture slots the material does not bind. Flat
        // blue normals and black emissive keep PBR correct with no textures.
        _defaultWhiteTexture = CreateSolidTexture(255, 255, 255, 255, srgb: false);
        _defaultBlackTexture = CreateSolidTexture(0, 0, 0, 255, srgb: true);
        _defaultNormalTexture = CreateSolidTexture(128, 128, 255, 255, srgb: false);
        CreateEnvironmentResources();

        _defaultMaterial = Material.CreateDefault(Shader.Load(PathUtil.Combine("Shaders", "Surface/Standard.wgsl")));
    }

    private void CreateEnvironmentResources()
    {
        _environmentSampler = _device.CreateSampler(new SamplerDescription
        {
            AddressMode = SamplerAddressMode.ClampToEdge,
            MipmapFilter = SamplerFilter.Linear
        });
        _environmentUniformBuffer = _device.CreateBuffer(new BufferDescription
        {
            Size = (ulong)Marshal.SizeOf<EnvironmentUniforms>(),
            Usage = BufferUsage.Uniform | BufferUsage.CopyDst
        });
        _defaultEnvironmentCube = CreateEnvironmentTexture(1, 1, TextureDimension.Cube, 6);
        _defaultIrradianceCube = CreateEnvironmentTexture(1, 1, TextureDimension.Cube, 6);
        _defaultPrefilteredCube = CreateEnvironmentTexture(1, 1, TextureDimension.Cube, 6);
        _defaultBrdfLut = CreateEnvironmentTexture(1, 1, TextureDimension.Dimension2D, 1);

        var skyShader = Shader.Load(PathUtil.Combine("Shaders", "Environment/Sky.wgsl"));
        // The sky renders into the linear HDR scene target (the tonemapper
        // runs later in the post-process pass).
        _skyPipeline = _device.CreatePipeline(CreateSkyPipelineDescription(
            skyShader, TextureFormat.Rgba16Float));
        _skyCameraBindGroup = _skyPipeline.CreateBindGroup(0,
        [
            new BindGroupBinding { Slot = 0, Buffer = _cameraBuffer, BufferSize = (ulong)sizeof(CameraUniforms) }
        ]);
        // The sky shader uses group 2 for the logical environment bindings,
        // leaving group 1 empty. WebGPU still requires every intermediate
        // group slot to be assigned before a later group can be used.
        _skyPlaceholderBindGroup = _skyPipeline.CreateBindGroup(1, []);
    }

    internal static PipelineDescription CreateSkyPipelineDescription(
        Shader shader,
        TextureFormat colorFormat) => new()
    {
        ShaderSource = shader.Source,
        VertexEntryPoint = "vs_main",
        FragmentEntryPoint = "fs_main",
        ColorFormat = colorFormat,
        DepthFormat = TextureFormat.Depth24Plus,
        DepthWriteEnabled = false,
        DepthCompare = CompareFunction.LessEqual,
        CullMode = CullMode.None,
        VertexLayout = new VertexBufferLayoutDescription { Stride = 0, Attributes = [] },
        BindGroups = shader.BuildBindGroupLayouts()
    };

    private void DrawSky(IRenderPass pass)
    {
        var shader = Shader.Load(PathUtil.Combine("Shaders", "Environment/Sky.wgsl"));
        pass.SetPipeline(_skyPipeline);
        pass.SetBindGroup(_skyCameraBindGroup, 0);
        pass.SetBindGroup(_skyPlaceholderBindGroup, 1);
        pass.SetBindGroup(GetEnvironmentBindGroup(_skyPipeline, shader),
            (uint)(shader.EnvironmentGroupIndex ?? throw new InvalidOperationException(
                "The sky shader does not declare the environment binding group.")));
        pass.Draw(3);
    }

    private ITexture CreateEnvironmentTexture(int width, int height, TextureDimension dimension, int layers)
    {
        var texture = _device.CreateTexture(new TextureDescription
        {
            Width = width,
            Height = height,
            Dimension = dimension,
            ArrayLayerCount = layers,
            Format = TextureFormat.Rgba16Float,
            Sampled = true,
            CopyDestination = true
        });
        var pixels = new byte[width * height * 8];
        unsafe
        {
            fixed (byte* data = pixels)
            {
                for (var layer = 0; layer < layers; layer++)
                    texture.Write((nint)data, width * 8, 0, 0, width, height, arrayLayer: layer);
            }
        }
        return texture;
    }

    /// <summary>
    /// The post-process groups for this frame: the world's enabled
    /// <see cref="PostProcess"/> components grouped by effect type, so each
    /// effect runs once with its settings blended through GetWeighted.
    /// Components attached to a <see cref="PostProcessVolume"/> entity
    /// participate only while the camera is inside their volume (weighted by
    /// position); the others are global. Groups run ordered by their driver's
    /// <see cref="PostProcess.Order"/>.
    /// </summary>
    private static List<PostProcessGroup> BuildPostProcessGroups(World? world, Vector3 cameraPosition)
    {
        var groups = new Dictionary<Type, PostProcessGroup>();
        if (world is null)
            return [];

        foreach (var component in world.Query<PostProcess>())
        {
            if (!component.Enabled)
                continue;

            var volume = component.Entity?.GetComponent<PostProcessVolume>();
            if (volume is { Enabled: false })
                continue; // a disabled volume hides its effects
            if (volume is not null)
            {
                if (!volume.TryGetWeight(cameraPosition, out var weight))
                    continue; // camera outside the volume: the effect does not apply
                Add(component, weight, isGlobal: false);
            }
            else
            {
                Add(component, 1f, isGlobal: true);
            }
        }

        return groups.Values.OrderBy(group => group.Driver.Order).ToList();

        void Add(PostProcess component, float weight, bool isGlobal)
        {
            var type = component.GetType();
            if (!groups.TryGetValue(type, out var group))
                groups.Add(type, group = new PostProcessGroup(type));
            group.Entries.Add(new PostProcessEntry(component, weight, isGlobal));
        }
    }

    /// <summary>One effect type's instances for this frame; the driver renders, entries feed GetWeighted.</summary>
    private sealed class PostProcessGroup
    {
        public PostProcessGroup(Type type) => Type = type;

        public Type Type { get; }

        public List<PostProcessEntry> Entries { get; } = [];

        /// <summary>The global instance if any, else the strongest-volume one.</summary>
        public PostProcess Driver => Entries
            .OrderByDescending(entry => entry.IsGlobal)
            .ThenByDescending(entry => entry.Weight)
            .First().Instance;
    }

    private void UpdateEnvironment(World? world)
    {
        var previous = _boundEnvironment;
        var environment = world?.Environment;

        // The procedural sky's sun follows the scene's first enabled
        // directional light (the sun disc lines up with the light that casts
        // shadows); a fixed fallback keeps the sky lit when the level has
        // none. The direction is world space: both the sky pass and the baked
        // IBL cubemap apply the environment rotation on top of it, so they
        // stay consistent and rotating the environment only ever re-samples.
        var sun = Vector4.Zero;
        var atmosphere = Vector4.Zero;
        if (environment?.Sky is ProceduralAtmosphere)
        {
            var sunDirection = world?.Query<DirectionalLight>()
                .FirstOrDefault(light => light.Enabled)?.Direction
                ?? DefaultSunDirection;
            environment.SunDirection = sunDirection;
            sun = new Vector4(
                sunDirection,
                environment.SunAngularRadius * MathF.PI / 180f);
            atmosphere = new Vector4(
                environment.Turbidity,
                environment.GroundAlbedo,
                environment.SunIntensity,
                0f);
        }

        if (environment is not null && environment.State == EnvironmentPreprocessingState.Ready)
            _boundEnvironment = environment;
        else
            _boundEnvironment = null;

        var environmentMap = _boundEnvironment?.EnvironmentMap;
        if (!ReferenceEquals(previous, _boundEnvironment) || !ReferenceEquals(_boundEnvironmentMap, environmentMap))
        {
            foreach (var bindGroup in _environmentBindGroups.Values)
                bindGroup.Dispose();
            _environmentBindGroups.Clear();
        }
        _boundEnvironmentMap = environmentMap;

        var source = _boundEnvironment;
        var uniforms = new EnvironmentUniforms
        {
            Parameters = new Vector4(
                source?.Rotation ?? 0f,
                source?.Intensity ?? 0f,
                source?.Exposure ?? 0f,
                source?.PrefilteredSpecularMap?.MipLevelCount is int m ? Math.Max(0, m - 1) : 0),
            Tint = source?.Tint ?? Vector4.One,
            Provider = new Vector4(source?.Sky is ProceduralAtmosphere ? 1f : 0f, 0f, 0f, 0f),
            Sun = sun,
            Atmosphere = atmosphere
        };
        _environmentUniformBuffer.Write(in uniforms);
    }

    private IBindGroup GetEnvironmentBindGroup(IPipeline pipeline, Shader shader)
    {
        if (_environmentBindGroups.TryGetValue(pipeline, out var existing))
            return existing;

        var groupIndex = shader.EnvironmentGroupIndex ?? throw new InvalidOperationException(
            $"Shader '{shader.Name}' does not declare the environment binding group.");

        var environment = _boundEnvironment;
        var bindGroup = pipeline.CreateBindGroup(groupIndex,
        [
            new BindGroupBinding { Slot = 0, Texture = environment?.EnvironmentMap ?? _defaultEnvironmentCube },
            new BindGroupBinding { Slot = 1, Texture = environment?.IrradianceMap ?? _defaultIrradianceCube },
            new BindGroupBinding { Slot = 2, Texture = environment?.PrefilteredSpecularMap ?? _defaultPrefilteredCube },
            new BindGroupBinding { Slot = 3, Texture = environment?.BrdfLut ?? _defaultBrdfLut },
            new BindGroupBinding { Slot = 4, Sampler = _environmentSampler },
            new BindGroupBinding { Slot = 5, Buffer = _environmentUniformBuffer, BufferSize = (ulong)Marshal.SizeOf<EnvironmentUniforms>() }
        ]);
        _environmentBindGroups.Add(pipeline, bindGroup);
        return bindGroup;
    }

    /// <summary>
    /// Creates the shadow-mapping resources: the depth atlas, its comparison
    /// sampler, the per-light shadow metadata buffer and the depth-only
    /// pipeline that renders each face into an atlas tile.
    /// </summary>
    private void CreateShadowResources()
    {
        _shadowAtlas = _device.CreateTexture(new TextureDescription
        {
            Width = ShadowAtlasSize,
            Height = ShadowAtlasSize,
            // Depth32Float is filterable, so the comparison sampler can do
            // bilinear PCF; Depth24Plus cannot be linearly filtered.
            Format = TextureFormat.Depth32Float,
            RenderTarget = true,
            Sampled = true
        });

        _shadowSampler = _device.CreateSampler(new SamplerDescription
        {
            AddressMode = SamplerAddressMode.ClampToEdge,
            Compare = CompareFunction.LessEqual
        });

        _shadowDataBuffer = _device.CreateBuffer(new BufferDescription
        {
            Size = (ulong)ShadowBufferSize,
            Usage = BufferUsage.Uniform | BufferUsage.CopyDst
        });

        var shader = Shader.Load(PathUtil.Combine("Shaders", "Surface/ShadowDepth.wgsl"));
        _shadowPipeline = _device.CreatePipeline(new PipelineDescription
        {
            ShaderSource = shader.Source,
            VertexEntryPoint = "vs_main",
            FragmentEntryPoint = "fs_main",
            DepthOnly = true,
            DepthFormat = TextureFormat.Depth32Float,
            DepthWriteEnabled = true,
            DepthCompare = CompareFunction.Less,
            VertexLayout = new VertexBufferLayoutDescription
            {
                // Shares the mesh vertex buffers (48-byte stride, position at 0).
                Stride = 12 * sizeof(float),
                Attributes =
                [
                    new VertexAttributeDescription { Format = VertexFormat.Float32x3, Offset = 0, ShaderLocation = 0 }
                ]
            },
            BindGroups = shader.BuildBindGroupLayouts()
        });

        // One view-projection buffer (and bind group) per atlas tile so every
        // face in a point light's cube keeps its own matrix.
        _shadowViewProjBuffers = new IBuffer[TotalShadowTiles];
        _shadowViewProjBindGroups = new IBindGroup[TotalShadowTiles];
        for (var tile = 0; tile < TotalShadowTiles; tile++)
        {
            _shadowViewProjBuffers[tile] = _device.CreateBuffer(new BufferDescription
            {
                Size = 64,
                Usage = BufferUsage.Uniform | BufferUsage.CopyDst
            });
            _shadowViewProjBindGroups[tile] = _shadowPipeline.CreateBindGroup(0,
            [
                new BindGroupBinding { Slot = 0, Buffer = _shadowViewProjBuffers[tile], BufferSize = 64 }
            ]);
        }
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

        var shader = Shader.Load(PathUtil.Combine("Shaders", "Editor/Grid.wgsl"));
        _gridPipeline = _device.CreatePipeline(new PipelineDescription
        {
            ShaderSource = shader.Source,
            VertexEntryPoint = "vs_main",
            FragmentEntryPoint = "fs_main",
            // Matches the linear HDR scene target (tonemapped in post).
            ColorFormat = TextureFormat.Rgba16Float,
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
            BindGroups = shader.BuildBindGroupLayouts()
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
        var uniforms = Grid.CreateUniforms(_cameraUniforms.View, _cameraUniforms.Projection);
        _gridUniformBuffer.Write(in uniforms);

        pass.SetPipeline(_gridPipeline);
        pass.SetBindGroup(_gridBindGroup, 0);
        pass.SetVertexBuffer(_gridVertexBuffer, _gridVertexBuffer.Size);
        pass.Draw(6);
    }

    /// <summary>Draws every living <see cref="MeshRenderer"/> in the world at its world transform.</summary>
    private void DrawMeshRenderers(IRenderPass pass, World? world, double time, List<Light> lights)
    {
        if (world is null)
            return;

        // Materialize once: components may be destroyed while we draw.
        var renderers = world.Query<MeshRenderer>().ToList();

        UpdateCameraUniforms(time);
        UpdateLights(lights);

        // Release GPU state for renderables whose component was destroyed.
        foreach (var stale in _renderables.Keys.Select(key => key.Renderer).Distinct().Except(renderers).ToArray())
            DisposeRenderable(stale);

        // Collect every draw item plus the set of meshes/textures/nodes still
        // referenced by the world, so orphaned GPU state can be pruned after.
        _liveMeshes.Clear();
        _liveTextures.Clear();
        _liveNodes.Clear();

        var cameraPosition = new Vector3(_cameraUniforms.CameraPosition.X, _cameraUniforms.CameraPosition.Y, _cameraUniforms.CameraPosition.Z);
        var opaque = new List<MeshDrawItem>();
        var transparent = new List<MeshDrawItem>();
        foreach (var renderer in renderers)
        {
            if (renderer.Model is null)
                continue;

            var worldMatrix = ToWorldMatrix(renderer.World);
            var depth = Vector3.DistanceSquared(renderer.World.Position, cameraPosition);
            foreach (var instance in renderer.Model.MeshInstances)
            {
                // A renderer material overrides every mesh; otherwise each mesh
                // uses its own model material, then the engine default. The
                // instance's node transform is evaluated here (not baked), so
                // one mesh can appear at several transforms and nodes can move.
                var material = renderer.Material ?? instance.Mesh.Material ?? _defaultMaterial!;
                _liveMeshes.Add(instance.Mesh);
                _liveNodes.Add(instance.Node);
                foreach (var texture in material.Textures.Values)
                    _liveTextures.Add(texture);

                if (!renderer.IsValid)
                    continue;

                var pipeline = GetMeshPipeline(material.Shader, material.Technique, material.BlendMode, material.DoubleSided);
                var item = new MeshDrawItem(
                    renderer,
                    instance.Mesh,
                    material,
                    instance.Node,
                    pipeline,
                    instance.Node.WorldTransform * worldMatrix,
                    depth);
                if (material.BlendMode == MaterialBlendMode.Blend)
                    transparent.Add(item);
                else
                    opaque.Add(item);
            }
        }

        PruneStaleResources();

        // Order the draws to minimize state changes: opaque meshes are grouped
        // by pipeline, then material, then mesh (depth is irrelevant because
        // they are depth-tested); transparent meshes stay sorted back-to-front
        // so src-over alpha blending composites correctly.
        var pipelineOrder = new Dictionary<IPipeline, int>();
        var materialOrder = new Dictionary<Material, int>();
        var meshOrder = new Dictionary<Mesh, int>();
        foreach (var item in opaque)
        {
            pipelineOrder.TryAdd(item.Pipeline, pipelineOrder.Count);
            materialOrder.TryAdd(item.Material, materialOrder.Count);
            meshOrder.TryAdd(item.Mesh, meshOrder.Count);
        }
        opaque.Sort((a, b) =>
        {
            var order = pipelineOrder[a.Pipeline].CompareTo(pipelineOrder[b.Pipeline]);
            if (order != 0)
                return order;
            order = materialOrder[a.Material].CompareTo(materialOrder[b.Material]);
            if (order != 0)
                return order;
            return meshOrder[a.Mesh].CompareTo(meshOrder[b.Mesh]);
        });
        transparent.Sort(static (a, b) => b.Depth.CompareTo(a.Depth));

        IPipeline? currentPipeline = null;
        foreach (var item in opaque.Concat(transparent))
        {
            var pipeline = item.Pipeline;
            var renderable = GetRenderableResources(item.Renderer, item.Material, item.Node, pipeline);

            if (currentPipeline != pipeline)
            {
                pass.SetPipeline(pipeline);
                pass.SetBindGroup(GetCameraBindGroup(pipeline), 0);
                if (item.Material.Shader.EnvironmentGroupIndex is int environmentGroup)
                    pass.SetBindGroup(GetEnvironmentBindGroup(pipeline, item.Material.Shader), (uint)environmentGroup);
                currentPipeline = pipeline;
            }
            pass.SetBindGroup(renderable.BindGroup, 1);

            var modelMatrix = item.ModelMatrix;
            renderable.ModelBuffer.Write(in modelMatrix);

            var buffers = GetMeshBuffers(item.Mesh);
            pass.SetVertexBuffer(buffers.VertexBuffer, buffers.VertexBuffer.Size);
            pass.SetIndexBuffer(buffers.IndexBuffer, buffers.IndexBuffer.Size);
            pass.DrawIndexed((uint)item.Mesh.Indices.Length);
        }
    }

    /// <summary>
    /// Releases GPU state (mesh buffers, material textures, selection-mask
    /// bind groups) whose CPU-side owner is no longer referenced by any mesh
    /// renderer in the world. Called once per frame after the live set is
    /// collected; a resource is simply re-uploaded if it is used again later.
    /// </summary>
    private void PruneStaleResources()
    {
        foreach (var mesh in _meshBuffers.Keys.Where(mesh => !_liveMeshes.Contains(mesh)).ToArray())
        {
            var buffers = _meshBuffers[mesh];
            buffers.VertexBuffer.Dispose();
            buffers.IndexBuffer.Dispose();
            _meshBuffers.Remove(mesh);
        }

        foreach (var texture in _materialTextures.Keys.Where(texture => !_liveTextures.Contains(texture)).ToArray())
        {
            _materialTextures[texture].Dispose();
            _materialTextures.Remove(texture);
        }

        foreach (var node in _selectionMaskModelBindGroups.Keys.Where(node => !_liveNodes.Contains(node)).ToArray())
        {
            _selectionMaskModelBindGroups[node].Dispose();
            _selectionMaskModelBindGroups.Remove(node);
        }

        foreach (var node in _selectionMaskModelBuffers.Keys.Where(node => !_liveNodes.Contains(node)).ToArray())
        {
            _selectionMaskModelBuffers[node].Dispose();
            _selectionMaskModelBuffers.Remove(node);
        }
    }

    /// <summary>
    /// Writes the shared camera uniforms (view/projection/position/clock)
    /// into the camera buffer once per frame.
    /// </summary>
    private void UpdateCameraUniforms(double time)
    {
        _cameraUniforms.Time = new Vector4((float)time, 0f, 0f, 0f);
        _cameraBuffer.Write(in _cameraUniforms);
    }

    /// <summary>
    /// Packs the world's lights (directional + point, capped at
    /// <see cref="MaxLights"/>) into the light buffer.
    /// </summary>
    private void UpdateLights(List<Light> lights)
    {

        // Lay the already-collected lights out exactly as Lighting.slang
        // expects: count at offset 0, array<LightData, 8> at offset 16.
        var collected = new LightGpuData[MaxLights];
        var count = Math.Min(lights.Count, MaxLights);
        for (var i = 0; i < count; i++)
        {
            switch (lights[i])
            {
                case PointLight point:
                    collected[i] = new LightGpuData
                    {
                        PositionType = new Vector4(point.World.Position, 1f),
                        ColorIntensity = new Vector4(point.Color, point.Intensity),
                        DirectionRange = new Vector4(0f, 0f, 0f, point.Range)
                    };
                    break;
                case DirectionalLight directional:
                    collected[i] = new LightGpuData
                    {
                        PositionType = new Vector4(0f, 0f, 0f, 0f),
                        ColorIntensity = new Vector4(directional.Color, directional.Intensity),
                        DirectionRange = new Vector4(directional.Direction, 0f)
                    };
                    break;
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

    /// <summary>Collects the world's enabled, supported lights (capped at <see cref="MaxLights"/>).</summary>
    private static List<Light> CollectLights(World? world)
    {
        var lights = new List<Light>();
        if (world is null)
            return lights;

        foreach (var light in world.Query<Light>())
        {
            if (!light.Enabled)
                continue;
            if (light is not (PointLight or DirectionalLight))
                continue;

            lights.Add(light);
            if (lights.Count >= MaxLights)
                break;
        }
        return lights;
    }

    /// <summary>
    /// Renders every shadow-casting light into the depth atlas and uploads the
    /// per-light shadow metadata the lit shaders sample. Lights that cannot get
    /// a tile (the atlas is full) silently fall back to unshadowed.
    /// </summary>
    private void UpdateShadows(ICommandBuffer commandBuffer, World? world, List<Light> lights)
    {
        var data = new ShadowLightGpuData[MaxLights];
        var tiles = new bool[TotalShadowTiles];
        var faces = new List<(Matrix4x4 ViewProj, int Tile, int PixelX, int PixelY, int PixelSize)>();

        // Collect the casters up front: a directional light fits its
        // orthographic box to the scene's world bounds, so the shadow map is
        // tight around the actual content instead of spanning the camera's
        // deep frustum (which wastes texels and balloons the bias).
        var renderers = world?.Query<MeshRenderer>()
            .Where(r => r.IsValid && r.Model is not null)
            .ToList() ?? [];
        var sceneBounds = ComputeWorldBounds(renderers);

        // Fallback (no casters): unproject the far-clamped camera frustum so
        // the box still covers whatever the camera sees.
        Matrix4x4.Invert(_cameraUniforms.View * ClampShadowProjection(_cameraUniforms.Projection), out var invViewProj);

        for (var i = 0; i < lights.Count; i++)
        {
            var light = lights[i];
            if (!light.CastShadows)
                continue;

            switch (light)
            {
                case DirectionalLight directional:
                    data[i] = BuildDirectionalShadow(directional, invViewProj, sceneBounds, tiles, faces);
                    break;
                case PointLight point:
                    data[i] = BuildPointShadow(point, tiles, faces);
                    break;
            }
        }

        WriteShadowBuffer(data);

        if (faces.Count == 0 || world is null)
            return;
        if (renderers.Count == 0)
            return;

        // Release GPU state for renderables whose component was destroyed.
        foreach (var stale in _shadowRenderables.Keys.Select(key => key.Renderer).Distinct().Except(renderers).ToArray())
            DisposeShadowRenderable(stale);

        // Upload each face's matrix into its own tile buffer up front:
        // QueueWriteBuffer is ordered before the command buffer runs, so a
        // single shared buffer would end up holding the last face's matrix for
        // every face.
        foreach (var face in faces)
            _shadowViewProjBuffers[face.Tile].Write(in face.ViewProj);

        var firstFace = true;
        foreach (var face in faces)
        {
            // Clear the whole atlas once (LoadOp.Clear ignores the scissor); the
            // remaining faces load so their tiles keep the previous faces.
            using (IRenderPass pass = commandBuffer.BeginRenderPass(new RenderPassDescription
            {
                Depth = new DepthAttachment
                {
                    Texture = _shadowAtlas,
                    LoadOp = firstFace ? RenderAttachmentLoadOp.Clear : RenderAttachmentLoadOp.Load,
                    StoreOp = RenderAttachmentStoreOp.Store,
                    ClearValue = 1f
                }
            }))
            {
                pass.SetViewport(face.PixelX, face.PixelY, face.PixelSize, face.PixelSize);
                pass.SetScissorRect((uint)face.PixelX, (uint)face.PixelY, (uint)face.PixelSize, (uint)face.PixelSize);
                DrawShadowMeshes(pass, renderers, face.Tile);
            }

            firstFace = false;
        }
    }

    /// <summary>World-space bounding box of every caster (the shadow pass renders them all).</summary>
    private static Bounds ComputeWorldBounds(IEnumerable<MeshRenderer> renderers)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var any = false;
        foreach (var renderer in renderers)
        {
            var bounds = renderer.Model!.Bounds.TransformBy(ToWorldMatrix(renderer.World));
            min = Vector3.Min(min, bounds.Min);
            max = Vector3.Max(max, bounds.Max);
            any = true;
        }

        return any ? new Bounds(min, max) : Bounds.Empty;
    }

    /// <summary>Builds the single orthographic shadow face for a directional light.</summary>
    private static ShadowLightGpuData BuildDirectionalShadow(
        DirectionalLight light,
        Matrix4x4 invViewProj,
        Bounds sceneBounds,
        bool[] tiles,
        List<(Matrix4x4 ViewProj, int Tile, int PixelX, int PixelY, int PixelSize)> faces)
    {
        var tile = AllocateShadowBlock(tiles, out var uvRect, out var pixelX, out var pixelY);
        if (tile is null)
            return default;
        var tileIndex = tile.Value;

        var direction = Vector3.Normalize(light.Direction);
        var up = MathF.Abs(direction.Y) > 0.9f ? Vector3.UnitX : Vector3.UnitY;
        var view = BuildLightView(Vector3.Zero, direction, up);

        // Fit the orthographic box to the scene's casters in light space: a
        // tight box spends the whole tile on the content (sharp edges, small
        // bias). With no casters, fall back to the camera frustum so the map
        // still covers whatever the camera sees.
        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        if (sceneBounds != Bounds.Empty)
        {
            for (var i = 0; i < 8; i++)
            {
                var corner = new Vector3(
                    (i & 1) == 0 ? sceneBounds.Min.X : sceneBounds.Max.X,
                    (i & 2) == 0 ? sceneBounds.Min.Y : sceneBounds.Max.Y,
                    (i & 4) == 0 ? sceneBounds.Min.Z : sceneBounds.Max.Z);
                var lightSpace = Vector3.Transform(corner, view);
                min = Vector3.Min(min, lightSpace);
                max = Vector3.Max(max, lightSpace);
            }
        }
        else
        {
            for (var zi = 0; zi < 2; zi++)
            {
                for (var yi = 0; yi < 2; yi++)
                {
                    for (var xi = 0; xi < 2; xi++)
                    {
                        var clip = new Vector4(xi * 2f - 1f, yi * 2f - 1f, zi, 1f);
                        var world = Vector4.Transform(clip, invViewProj);
                        var point = new Vector3(world.X, world.Y, world.Z) / world.W;
                        var lightSpace = Vector3.Transform(point, view);
                        min = Vector3.Min(min, lightSpace);
                        max = Vector3.Max(max, lightSpace);
                    }
                }
            }
        }

        var spanZ = max.Z - min.Z;
        if (spanZ < 0.001f)
            spanZ = 1f;
        var margin = Math.Max(1f, spanZ * 0.25f);

        // Guard against a degenerate projection (light edge-on to the scene),
        // which would otherwise divide by zero in the ortho matrix.
        var extentX = Math.Max(max.X - min.X, 1f);
        var extentY = Math.Max(max.Y - min.Y, 1f);
        // A small XY margin keeps the PCF taps near the box edge inside the
        // tile instead of bleeding into the neighbouring atlas tile.
        var xyMargin = Math.Max(0.5f, Math.Max(extentX, extentY) * 0.1f);
        var centerX = (min.X + max.X) * 0.5f;
        var centerY = (min.Y + max.Y) * 0.5f;
        var projection = CreateOrthoShadow(
            centerX - extentX * 0.5f - xyMargin, centerX + extentX * 0.5f + xyMargin,
            centerY - extentY * 0.5f - xyMargin, centerY + extentY * 0.5f + xyMargin,
            min.Z - margin, max.Z + margin);
        var viewProj = view * projection;

        faces.Add((viewProj, tileIndex, pixelX, pixelY, DirectionalShadowTileSize));
        return new ShadowLightGpuData
        {
            Flags = new Vector4(1f, 0f, 1f, DirectionalShadowBias),
            Face0 = new ShadowFaceGpuData { UvRect = uvRect, ViewProj = viewProj }
        };
    }

    /// <summary>Builds the six cube faces for a point light's shadow map.</summary>
    private static ShadowLightGpuData BuildPointShadow(
        PointLight light,
        bool[] tiles,
        List<(Matrix4x4 ViewProj, int Tile, int PixelX, int PixelY, int PixelSize)> faces)
    {
        var tile = new int[6];
        for (var i = 0; i < tile.Length; i++)
        {
            var allocated = AllocateShadowTile(tiles);
            if (allocated is null)
            {
                // Roll back the tiles already claimed for this light.
                for (var j = 0; j < i; j++)
                    tiles[tile[j]] = false;
                return default;
            }
            tile[i] = allocated.Value;
        }

        var far = Math.Max(light.Range, 0.1f);
        var near = Math.Min(0.1f, far * 0.01f);
        var result = new ShadowLightGpuData { Flags = new Vector4(1f, 1f, 6f, PointShadowBias) };

        for (var i = 0; i < 6; i++)
        {
            var (forward, up) = PointFaceOrientations[i];
            var view = BuildLightView(light.World.Position, forward, up);
            var projection = CreatePointShadowProjection(near, far);
            var viewProj = view * projection;
            var face = new ShadowFaceGpuData { UvRect = ShadowTileUvRect(tile[i]), ViewProj = viewProj };
            SetShadowFace(ref result, i, face);
            faces.Add((viewProj, tile[i], ShadowTileX(tile[i]), ShadowTileY(tile[i]), ShadowTileSize));
        }

        return result;
    }

    private static void SetShadowFace(ref ShadowLightGpuData light, int index, in ShadowFaceGpuData face)
    {
        switch (index)
        {
            case 0: light.Face0 = face; break;
            case 1: light.Face1 = face; break;
            case 2: light.Face2 = face; break;
            case 3: light.Face3 = face; break;
            case 4: light.Face4 = face; break;
            case 5: light.Face5 = face; break;
        }
    }

    private static int? AllocateShadowTile(bool[] tiles)
    {
        for (var i = 0; i < tiles.Length; i++)
        {
            if (tiles[i])
                continue;
            tiles[i] = true;
            return i;
        }
        return null;
    }

    /// <summary>
    /// Allocates a 2x2 block of atlas tiles (a 1024px map) for a directional
    /// light, falling back to a single tile when the atlas has no free block.
    /// The UV rect and pixel origin are the block's, so the light's content
    /// spans the whole 1024px region.
    /// </summary>
    private static int? AllocateShadowBlock(bool[] tiles, out Vector4 uvRect, out int pixelX, out int pixelY)
    {
        for (var row = 0; row + 1 < ShadowTilesPerRow; row++)
        {
            for (var col = 0; col + 1 < ShadowTilesPerRow; col++)
            {
                var topLeft = row * ShadowTilesPerRow + col;
                var occupied = tiles[topLeft] || tiles[topLeft + 1]
                    || tiles[topLeft + ShadowTilesPerRow] || tiles[topLeft + ShadowTilesPerRow + 1];
                if (occupied)
                    continue;

                tiles[topLeft] = true;
                tiles[topLeft + 1] = true;
                tiles[topLeft + ShadowTilesPerRow] = true;
                tiles[topLeft + ShadowTilesPerRow + 1] = true;

                var u = 1f / ShadowTilesPerRow;
                uvRect = new Vector4(col * u, row * u, (col + 2) * u, (row + 2) * u);
                pixelX = col * ShadowTileSize;
                pixelY = row * ShadowTileSize;
                return topLeft;
            }
        }

        var single = AllocateShadowTile(tiles);
        if (single is null)
        {
            uvRect = default;
            pixelX = 0;
            pixelY = 0;
            return null;
        }

        uvRect = ShadowTileUvRect(single.Value);
        pixelX = ShadowTileX(single.Value);
        pixelY = ShadowTileY(single.Value);
        return single;
    }

    private static Vector4 ShadowTileUvRect(int tile)
    {
        var col = tile % ShadowTilesPerRow;
        var row = tile / ShadowTilesPerRow;
        var u = 1f / ShadowTilesPerRow;
        return new Vector4(col * u, row * u, (col + 1) * u, (row + 1) * u);
    }

    private static int ShadowTileX(int tile) => tile % ShadowTilesPerRow * ShadowTileSize;
    private static int ShadowTileY(int tile) => tile / ShadowTilesPerRow * ShadowTileSize;

    /// <summary>Uploads the shadow atlas metadata (atlas size + per-light faces).</summary>
    private void WriteShadowBuffer(ShadowLightGpuData[] data)
    {
        var bytes = new byte[ShadowBufferSize];
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0, 4), ShadowAtlasSize);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(4, 4), ShadowAtlasSize);
        // atlasSize.z carries the slope-bias scale to the lit shaders.
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(8, 4), ShadowSlopeScale);
        unsafe
        {
            fixed (byte* destination = bytes)
            {
                var lightPtr = (ShadowLightGpuData*)(destination + 16);
                for (var i = 0; i < MaxLights; i++)
                    lightPtr[i] = data[i];
            }
        }
        _shadowDataBuffer.Write(bytes);
    }

    /// <summary>Draws every caster mesh into the current shadow face.</summary>
    private void DrawShadowMeshes(IRenderPass pass, List<MeshRenderer> renderers, int tile)
    {
        pass.SetPipeline(_shadowPipeline);
        pass.SetBindGroup(_shadowViewProjBindGroups[tile], 0);

        foreach (var renderer in renderers)
        {
            var worldMatrix = ToWorldMatrix(renderer.World);
            foreach (var instance in renderer.Model!.MeshInstances)
            {
                var shadowRenderable = GetShadowRenderable(renderer, instance.Node);
                var modelMatrix = instance.Node.WorldTransform * worldMatrix;
                shadowRenderable.ModelBuffer.Write(in modelMatrix);
                pass.SetBindGroup(shadowRenderable.BindGroup, 1);

                var buffers = GetMeshBuffers(instance.Mesh);
                pass.SetVertexBuffer(buffers.VertexBuffer, buffers.VertexBuffer.Size);
                pass.SetIndexBuffer(buffers.IndexBuffer, buffers.IndexBuffer.Size);
                pass.DrawIndexed((uint)instance.Mesh.Indices.Length);
            }
        }
    }

    /// <summary>Creates (or returns) the shadow pass's per-instance model bind group.</summary>
    private ShadowRenderable GetShadowRenderable(MeshRenderer renderer, ModelNode node)
    {
        var key = (renderer, node);
        if (_shadowRenderables.TryGetValue(key, out var existing))
            return existing;

        var modelBuffer = _device.CreateBuffer(new BufferDescription
        {
            Size = 64,
            Usage = BufferUsage.Uniform | BufferUsage.CopyDst
        });
        var shadowRenderable = new ShadowRenderable
        {
            ModelBuffer = modelBuffer,
            BindGroup = _shadowPipeline.CreateBindGroup(1,
            [
                new BindGroupBinding { Slot = 0, Buffer = modelBuffer, BufferSize = 64 }
            ])
        };
        _shadowRenderables.Add(key, shadowRenderable);
        return shadowRenderable;
    }

    private void DisposeShadowRenderable(MeshRenderer renderer)
    {
        foreach (var key in _shadowRenderables.Keys.Where(key => key.Renderer == renderer).ToArray())
        {
            if (!_shadowRenderables.Remove(key, out var shadowRenderable))
                continue;

            shadowRenderable.BindGroup.Dispose();
            shadowRenderable.ModelBuffer.Dispose();
        }
    }

    /// <summary>Left-handed look-at view (mirrors <see cref="Camera.ViewMatrix"/>).</summary>
    private static Matrix4x4 BuildLightView(Vector3 eye, Vector3 forward, Vector3 up)
    {
        var f = Vector3.Normalize(forward);
        var right = Vector3.Normalize(Vector3.Cross(up, f));
        var upVector = Vector3.Cross(f, right);
        return new Matrix4x4(
            right.X, upVector.X, f.X, 0f,
            right.Y, upVector.Y, f.Y, 0f,
            right.Z, upVector.Z, f.Z, 0f,
            -Vector3.Dot(right, eye), -Vector3.Dot(upVector, eye), -Vector3.Dot(f, eye), 1f);
    }

    /// <summary>
    /// Rebuilds the camera projection with its far plane clamped to
    /// <see cref="DirectionalShadowMaxDistance"/> (FOV, aspect and near are
    /// preserved). Directional shadows are fit to this clamped frustum so a
    /// small scene is not smeared across the camera's full 100-unit far plane.
    /// </summary>
    private static Matrix4x4 ClampShadowProjection(Matrix4x4 projection)
    {
        // Left-handed perspective (Camera.ProjectionMatrix): m33 = far/(far-near)
        // and m43 = -(near*far)/(far-near). Solve for near/far, then rebuild.
        var zScale = projection.M33;
        var zOffset = projection.M43;
        if (MathF.Abs(zScale) < 1e-6f)
            return projection;

        var near = -zOffset / zScale;
        var far = -zOffset / (zScale - 1f);
        if (far <= near)
            return projection;

        var clampedFar = MathF.Min(far, DirectionalShadowMaxDistance);
        if (clampedFar >= far)
            return projection;

        var newZScale = clampedFar / (clampedFar - near);
        var newZOffset = -(near * clampedFar) / (clampedFar - near);
        return new Matrix4x4(
            projection.M11, 0f, 0f, 0f,
            0f, projection.M22, 0f, 0f,
            0f, 0f, newZScale, 1f,
            0f, 0f, newZOffset, 0f);
    }

    /// <summary>Left-handed orthographic projection, z in [0,1] (row-vector layout).</summary>
    private static Matrix4x4 CreateOrthoShadow(float left, float right, float bottom, float top, float near, float far)
    {
        var rl = right - left;
        var tb = top - bottom;
        var fn = far - near;
        return new Matrix4x4(
            2f / rl, 0f, 0f, 0f,
            0f, 2f / tb, 0f, 0f,
            0f, 0f, 1f / fn, 0f,
            -(right + left) / rl, -(top + bottom) / tb, -near / fn, 1f);
    }

    /// <summary>90-degree perspective projection for a point light's square cube face.</summary>
    private static Matrix4x4 CreatePointShadowProjection(float near, float far)
    {
        var yScale = 1f / MathF.Tan(MathF.PI / 4f);
        var zScale = far / (far - near);
        var zOffset = -(near * far) / (far - near);
        return new Matrix4x4(
            yScale, 0f, 0f, 0f,
            0f, yScale, 0f, 0f,
            0f, 0f, zScale, 1f,
            0f, 0f, zOffset, 0f);
    }

    /// <summary>
    /// Returns (creating on first use) the pipeline for a shader, technique and
    /// rasterizer state (blend mode + double-sidedness). Blended materials skip
    /// depth writes (they still test depth so they respect opaque geometry) and
    /// single-sided materials cull back faces. The group-1 layout is derived
    /// from the shader's own bindings, so the WGSL source is the single source
    /// of truth for the pipeline layout.
    /// </summary>
    private IPipeline GetMeshPipeline(
        Shader shader,
        string techniqueName,
        MaterialBlendMode blendMode,
        bool doubleSided)
    {
        var key = new MeshPipelineKey(shader, techniqueName, blendMode, doubleSided);
        if (_meshPipelines.TryGetValue(key, out var existing))
            return existing;

        var technique = shader.GetTechnique(techniqueName);

        // Group 0 is the engine's shared per-frame state (camera, lights,
        // shadow map) and includes the one binding slangc's reflection cannot
        // classify — the shadow comparison sampler — so it stays declared in
        // FrameGroupBindings. Group 1 (per-renderable: model, material,
        // textures) derives from the shader's reflected bindings.
        var bindGroups = shader.BuildBindGroupLayouts();

        var pipeline = _device.CreatePipeline(new PipelineDescription
        {
            ShaderSource = shader.Source,
            VertexEntryPoint = technique.VertexEntryPoint,
            FragmentEntryPoint = technique.FragmentEntryPoint,
            // The scene pass renders linear HDR into the float scene target;
            // the post-process pass applies the display transform.
            ColorFormat = TextureFormat.Rgba16Float,
            DepthFormat = TextureFormat.Depth24Plus,
            AlphaBlend = blendMode == MaterialBlendMode.Blend,
            DepthWriteEnabled = blendMode == MaterialBlendMode.Opaque,
            DepthCompare = CompareFunction.Less,
            CullMode = doubleSided ? CullMode.None : CullMode.Back,
            VertexLayout = MeshVertexLayout,
            BindGroups = [FrameGroupBindings, .. bindGroups.Skip(1)]
        });
        _meshPipelines.Add(key, pipeline);
        return pipeline;
    }

    /// <summary>Creates (or returns) the per-frame bind group 0 for a mesh pipeline.</summary>
    private IBindGroup GetCameraBindGroup(IPipeline pipeline)
    {
        if (_cameraBindGroups.TryGetValue(pipeline, out var existing))
            return existing;

        var bindGroup = pipeline.CreateBindGroup(0,
        [
            new BindGroupBinding { Slot = 0, Buffer = _cameraBuffer, BufferSize = (ulong)sizeof(CameraUniforms) },
            new BindGroupBinding { Slot = 1, Buffer = _lightsBuffer, BufferSize = (ulong)LightsBufferSize },
            new BindGroupBinding { Slot = 2, Buffer = _shadowDataBuffer, BufferSize = (ulong)ShadowBufferSize },
            new BindGroupBinding { Slot = 3, Texture = _shadowAtlas },
            new BindGroupBinding { Slot = 4, Sampler = _shadowSampler }
        ]);
        _cameraBindGroups.Add(pipeline, bindGroup);
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
    /// Rebuilt when the component switches shader/technique or the material's
    /// parameters/textures change; otherwise the packed uniforms are uploaded
    /// once and reused across frames.
    /// </summary>
    private RenderableResources GetRenderableResources(MeshRenderer renderer, Material material, ModelNode node, IPipeline pipeline)
    {
        var key = (renderer, material, node);
        if (_renderables.TryGetValue(key, out var existing) &&
            existing.Shader == material.Shader &&
            existing.Technique == material.Technique &&
            existing.MaterialRevision == material.Revision)
            return existing;

        if (existing is not null)
            DisposeRenderable(key);

        var shader = material.Shader;
        var fields = shader.MaterialFields;
        var materialBuffer = fields.Count == 0
            ? null
            : _device.CreateBuffer(new BufferDescription
            {
                Size = (ulong)UniformPacker.ComputeStructSize(fields),
                Usage = BufferUsage.Uniform | BufferUsage.CopyDst
            });
        if (materialBuffer is not null)
            materialBuffer.Write(UniformPacker.Pack(fields, material.Values));

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
            MaterialRevision = material.Revision,
            ModelBuffer = modelBuffer,
            MaterialBuffer = materialBuffer,
            BindGroup = pipeline.CreateBindGroup(1, bindings)
        };
        _renderables.Add(key, resources);
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

            try
            {
                var srgb = IsColorTextureSlot(slotName);
                var mipLevels = cpuTexture.MipLevelCount;
                var gpu = _device.CreateTexture(new TextureDescription
                {
                    Width = cpuTexture.Width,
                    Height = cpuTexture.Height,
                    Format = srgb ? TextureFormat.Rgba8UnormSrgb : TextureFormat.Rgba8Unorm,
                    Sampled = true,
                    CopyDestination = true,
                    MipLevelCount = mipLevels
                });
                unsafe
                {
                    for (var mip = 0; mip < mipLevels; mip++)
                    {
                        var mipWidth = cpuTexture.GetMipWidth(mip);
                        var mipHeight = cpuTexture.GetMipHeight(mip);
                        var mipPixels = cpuTexture.GetMipPixels(mip);
                        fixed (byte* pixels = mipPixels)
                            gpu.Write((nint)pixels, mipWidth * 4, 0, 0, mipWidth, mipHeight, mip);
                    }
                }
                _materialTextures.Add(cpuTexture, gpu);
                return gpu;
            }
            catch (Exception)
            {
                // Lazy decode (or the upload) failed for this texture: leave the
                // slot on its neutral default so the model still renders.
            }
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

    /// <summary>Disposes the GPU state of every renderable owned by a component.</summary>
    private void DisposeRenderable(MeshRenderer renderer)
    {
        foreach (var key in _renderables.Keys.Where(key => key.Renderer == renderer).ToArray())
            DisposeRenderable(key);
    }

    /// <summary>Disposes the GPU state of one renderable and drops its cache entry.</summary>
    private void DisposeRenderable((MeshRenderer Renderer, Material Material, ModelNode Node) key)
    {
        if (!_renderables.Remove(key, out var resources))
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
        _postProcessPointSampler ??= _device.CreateSampler(new SamplerDescription { Filter = SamplerFilter.Nearest });

        var sceneShader = Shader.Load(PathUtil.Combine("Shaders", "Ui/BlitScene.wgsl"));
        _scenePipeline = _device.CreatePipeline(new PipelineDescription
        {
            ShaderSource = sceneShader.Source,
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
            BindGroups = sceneShader.BuildBindGroupLayouts()
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
        var shader = Shader.Load(PathUtil.Combine("Shaders", "Ui/BlitLinear.wgsl"));
        _ui2dSampler ??= _device.CreateSampler(new SamplerDescription());
        _ui2dPipeline ??= _device.CreatePipeline(new PipelineDescription
        {
            ShaderSource = shader.Source,
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
            BindGroups = shader.BuildBindGroupLayouts()
        });
    }

    private void CreateBackdropResources()
    {
        var shader = Shader.Load(PathUtil.Combine("Shaders", "Ui/Backdrop.wgsl"));
        _backdropParamsBuffer = _device.CreateBuffer(new BufferDescription
        {
            Size = (ulong)(MaxBackdropRegions * sizeof(BackdropGpuParams)),
            Usage = BufferUsage.Storage | BufferUsage.CopyDst
        });
        _backdropParamsBytes = null;

        _backdropPipeline = _device.CreatePipeline(new PipelineDescription
        {
            ShaderSource = shader.Source,
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
            BindGroups = shader.BuildBindGroupLayouts()
        });
    }

    /// <summary>Full-window depth attachment for the surface composite pass.</summary>
    private void CreateSurfaceDepth(int width, int height)
    {
        _surfaceDepth?.Dispose();
        _surfaceDepth = _device.CreateTexture(new TextureDescription
        {
            Width = Math.Max(1, width),
            Height = Math.Max(1, height),
            Format = TextureFormat.Depth24Plus,
            RenderTarget = true
        });
    }

    /// <summary>Viewport-sized depth attachment for the 3D scene and selection-mask passes.</summary>
    private void CreateSceneDepth(int width, int height)
    {
        _sceneDepth?.Dispose();
        _sceneDepth = _device.CreateTexture(new TextureDescription
        {
            Width = Math.Max(1, width),
            Height = Math.Max(1, height),
            Format = TextureFormat.Depth24Plus,
            RenderTarget = true
        });
    }

    /// <summary>Returns (creating on first use) the pipeline for a post-process shader.</summary>
    private IPipeline GetOrCreatePostProcessPipeline(string shaderPath)
    {
        if (_postProcessPipelines.TryGetValue(shaderPath, out var pipeline))
            return pipeline;
        var shader = Shader.Load(PathUtil.Combine(shaderPath));
        if (!shader.EntryPoints.Any(entry => entry.Name == "vs_main" && entry.Stage == ShaderStageKind.Vertex) ||
            !shader.EntryPoints.Any(entry => entry.Name == "fs_main" && entry.Stage == ShaderStageKind.Fragment))
            throw new InvalidOperationException(
                $"Post-process shader '{shaderPath}' must declare vs_main and fs_main entry points.");
        pipeline = _device.CreatePipeline(new PipelineDescription
        {
            ShaderSource = shader.Source,
            VertexEntryPoint = "vs_main",
            FragmentEntryPoint = "fs_main",
            // Every chain texture (scene, intermediates, display) is linear
            // Rgba16Float, so one pipeline serves all passes.
            ColorFormat = TextureFormat.Rgba16Float,
            DepthFormat = TextureFormat.Depth24Plus,
            DepthCompare = CompareFunction.Always,
            DepthWriteEnabled = false,
            VertexLayout = new VertexBufferLayoutDescription { Stride = 0, Attributes = [] },
            BindGroups = shader.BuildBindGroupLayouts()
        });
        _postProcessPipelines.Add(shaderPath, pipeline);
        return pipeline;
    }

    /// <summary>
    /// Runs one fullscreen post-process pass: loads (and caches) the pipeline
    /// for <paramref name="shaderPath"/>, packs <paramref name="attributes"/>
    /// into the shader's group-0 uniform buffer by field name, binds the input
    /// texture, scene depth and sampler by reflection, and draws a fullscreen
    /// triangle into <paramref name="to"/>.
    /// </summary>
    internal void RunPostProcessPass(
        ICommandBuffer commandBuffer,
        ITexture from,
        ITexture to,
        string shaderPath,
        RenderAttributes? attributes,
        PostProcessSampler sampler)
    {
        var shader = Shader.Load(PathUtil.Combine(shaderPath));
        var pipeline = GetOrCreatePostProcessPipeline(shaderPath);
        var samplerState = sampler == PostProcessSampler.Point ? _postProcessPointSampler : _uiSampler;

        // Pack the settings uniform: the shader's group-0 uniform buffer
        // struct, field by field, from the attribute bag. Field names match
        // attribute names with trailing underscores ignored ("operator_" ↔
        // "Operator") and case ignored, so the C# and shader sides only need
        // to agree on names.
        var uniformBinding = shader.Bindings.FirstOrDefault(binding =>
            binding.Group == 0 && binding.Kind == ShaderBindingKind.UniformBuffer);
        IBuffer? uniformBuffer = null;
        var uniformSize = 0ul;
        if (uniformBinding is not null)
        {
            var fields = shader.Structs
                .FirstOrDefault(structure => structure.Name == uniformBinding.TypeName)?.Fields;
            if (fields is { Count: > 0 })
            {
                uniformSize = (ulong)UniformPacker.ComputeStructSize(fields);
                uniformBuffer = _device.CreateBuffer(new BufferDescription
                {
                    Size = uniformSize,
                    Usage = BufferUsage.Uniform | BufferUsage.CopyDst
                });
                uniformBuffer.Write(UniformPacker.Pack(fields, PackAttributes(fields, attributes)));
                _postProcessFrameResources.Add(uniformBuffer);
            }
        }

        var bindings = new List<BindGroupBinding>();
        foreach (var binding in shader.Bindings.Where(binding => binding.Group == 0))
        {
            switch (binding.Kind)
            {
                case ShaderBindingKind.Texture when binding.TypeName == "texture_depth_2d":
                    bindings.Add(new BindGroupBinding { Slot = binding.Slot, Texture = _sceneDepth });
                    break;
                case ShaderBindingKind.Texture:
                    bindings.Add(new BindGroupBinding { Slot = binding.Slot, Texture = from });
                    break;
                case ShaderBindingKind.Sampler:
                    bindings.Add(new BindGroupBinding { Slot = binding.Slot, Sampler = samplerState });
                    break;
                case ShaderBindingKind.UniformBuffer when uniformBuffer is not null:
                    bindings.Add(new BindGroupBinding { Slot = binding.Slot, Buffer = uniformBuffer, BufferSize = uniformSize });
                    break;
            }
        }
        var bindGroup = pipeline.CreateBindGroup(bindings);
        _postProcessFrameResources.Add(bindGroup);

        using (IRenderPass pass = commandBuffer.BeginRenderPass(new RenderPassDescription
        {
            Color = new ColorAttachment
            {
                Texture = to,
                LoadOp = RenderAttachmentLoadOp.Clear,
                StoreOp = RenderAttachmentStoreOp.Store,
                ClearColor = Vector4.Zero
            },
            // The pipeline declares a depth format; the scene depth is attached
            // but untouched (compare always, no writes).
            Depth = new DepthAttachment
            {
                Texture = _sceneDepth,
                LoadOp = RenderAttachmentLoadOp.Load,
                StoreOp = RenderAttachmentStoreOp.Store
            }
        }))
        {
            pass.SetPipeline(pipeline);
            pass.SetBindGroup(bindGroup, 0);
            pass.Draw(3);
        }
    }

    /// <summary>Maps the attribute bag onto the uniform struct's fields by name.</summary>
    private static Dictionary<string, ShaderParameter> PackAttributes(
        IReadOnlyList<ShaderStructField> fields,
        RenderAttributes? attributes)
    {
        var values = new Dictionary<string, ShaderParameter>(StringComparer.Ordinal);
        if (attributes is null)
            return values;
        foreach (var field in fields)
        {
            foreach (var (name, parameter) in attributes.Values)
            {
                if (!RenderAttributes.MatchesField(field.Name, name))
                    continue;
                values[field.Name] = parameter;
                break;
            }
        }

        return values;
    }

    /// <summary>Returns the scratch texture at <paramref name="index"/> (0..3) for multi-pass effects.</summary>
    internal ITexture GetPostProcessScratchTexture(int index)
    {
        if ((uint)index >= (uint)_postProcessScratch.Length)
            throw new ArgumentOutOfRangeException(nameof(index), index, "The post-process scratch pool has 4 textures.");
        return _postProcessScratch[index];
    }

    /// <summary>
    /// Drops the cached pipeline and shader for a post-process shader path
    /// (called by the editor's shader hot reload after recompiling); the next
    /// frame reloads the shader and recreates the pipeline.
    /// </summary>
    public void InvalidatePostProcessShader(string shaderPath)
    {
        if (_postProcessPipelines.Remove(shaderPath, out var pipeline))
            pipeline.Dispose();
        Shader.Invalidate(PathUtil.Combine(shaderPath));
    }

    /// <summary>Releases the per-frame uniform buffers and bind groups of the post-process chain.</summary>
    private void DisposePostProcessFrameResources()
    {
        foreach (var resource in _postProcessFrameResources)
            resource.Dispose();
        _postProcessFrameResources.Clear();
    }

    /// <summary>
    /// Creates the viewport-sized scene targets (linear HDR scene + display
    /// texture) and the bind groups that sample them: the post-process input
    /// (scene), and the surface-side blit/backdrop/outline (display).
    /// </summary>
    private void CreateSceneResources(int width, int height)
    {
        _sceneBindGroup?.Dispose();
        _backdropBindGroup?.Dispose();
        _postProcessTextureA?.Dispose();
        _postProcessTextureB?.Dispose();
        _sceneTexture?.Dispose();
        _displayTexture?.Dispose();
        foreach (var scratch in _postProcessScratch)
            scratch.Dispose();

        width = Math.Max(1, width);
        height = Math.Max(1, height);

        // Linear HDR: the scene (materials + sky) renders un-tonemapped so
        // the post-process pass can apply one display transform to the whole
        // frame without clipping highlights.
        _sceneTexture = _device.CreateTexture(new TextureDescription
        {
            Width = width,
            Height = height,
            Format = TextureFormat.Rgba16Float,
            RenderTarget = true,
            Sampled = true
        });

        // Display-referred texture the last post-process pass writes. Linear
        // Rgba16Float like the scene and the intermediates, so one pipeline
        // serves every pass; the scene blit (Ui/BlitScene.slang) applies the
        // single linear -> sRGB display encode when presenting it to the
        // (non-sRGB) surface.
        _displayTexture = _device.CreateTexture(new TextureDescription
        {
            Width = width,
            Height = height,
            Format = TextureFormat.Rgba16Float,
            RenderTarget = true,
            Sampled = true
        });

        // HDR intermediates for chains of two or more post-processes: the
        // passes ping-pong through them before the final pass writes the
        // display texture.
        _postProcessTextureA = _device.CreateTexture(new TextureDescription
        {
            Width = width,
            Height = height,
            Format = TextureFormat.Rgba16Float,
            RenderTarget = true,
            Sampled = true
        });
        _postProcessTextureB = _device.CreateTexture(new TextureDescription
        {
            Width = width,
            Height = height,
            Format = TextureFormat.Rgba16Float,
            RenderTarget = true,
            Sampled = true
        });

        // Scratch pool for multi-pass effects (blur, bloom, ...): 4 extra
        // Rgba16Float targets, addressed by PostProcessContext.GetScratchTexture.
        _postProcessScratch = new ITexture[4];
        for (var index = 0; index < _postProcessScratch.Length; index++)
        {
            _postProcessScratch[index] = _device.CreateTexture(new TextureDescription
            {
                Width = width,
                Height = height,
                Format = TextureFormat.Rgba16Float,
                RenderTarget = true,
                Sampled = true
            });
        }

        _sceneBindGroup = _scenePipeline.CreateBindGroup(
        [
            new BindGroupBinding { Slot = 0, Texture = _displayTexture },
            new BindGroupBinding { Slot = 1, Sampler = _uiSampler }
        ]);

        _backdropBindGroup = _backdropPipeline.CreateBindGroup(
        [
            new BindGroupBinding { Slot = 0, Texture = _displayTexture },
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

        _outlineParamsBuffer ??= _device.CreateBuffer(new BufferDescription
        {
            Size = (ulong)sizeof(SelectionOutlineParams),
            Usage = BufferUsage.Uniform | BufferUsage.CopyDst
        });

        // The mask pipeline reuses the mesh vertex buffers (48-byte stride,
        // position at location 0) and the shared scene buffer; only the model
        // uniform is per-renderable.
        var shader = Shader.Load(PathUtil.Combine("Shaders", "Editor/SelectionMask.wgsl"));
        _selectionMaskPipeline ??= _device.CreatePipeline(new PipelineDescription
        {
            ShaderSource = shader.Source,
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
            BindGroups = shader.BuildBindGroupLayouts()
        });

        var outlineShader = Shader.Load(PathUtil.Combine("Shaders", "Editor/SelectionOutline.wgsl"));
        _outlinePipeline ??= _device.CreatePipeline(new PipelineDescription
        {
            ShaderSource = outlineShader.Source,
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
            BindGroups = outlineShader.BuildBindGroupLayouts()
        });

        _selectionMaskSceneBindGroup = _selectionMaskPipeline.CreateBindGroup(0,
        [
            new BindGroupBinding { Slot = 0, Buffer = _cameraBuffer, BufferSize = (ulong)sizeof(CameraUniforms) }
        ]);
        _outlineBindGroup = _outlinePipeline.CreateBindGroup(
        [
            // The outline replaces the scene blit on the surface, so it
            // samples the post-processed display texture (the "keep the scene
            // as-is" branch shows the tonemapped frame).
            new BindGroupBinding { Slot = 0, Texture = _displayTexture },
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

        var worldMatrix = ToWorldMatrix(renderer.World);
        foreach (var instance in renderer.Model.MeshInstances)
        {
            pass.SetBindGroup(GetSelectionMaskModelBindGroup(instance.Node), 1);

            var modelMatrix = instance.Node.WorldTransform * worldMatrix;
            GetSelectionMaskModelBuffer(instance.Node).Write(in modelMatrix);

            var buffers = GetMeshBuffers(instance.Mesh);
            pass.SetVertexBuffer(buffers.VertexBuffer, buffers.VertexBuffer.Size);
            pass.SetIndexBuffer(buffers.IndexBuffer, buffers.IndexBuffer.Size);
            pass.DrawIndexed((uint)instance.Mesh.Indices.Length);
        }
    }

    private IBuffer GetSelectionMaskModelBuffer(ModelNode node)
    {
        if (_selectionMaskModelBuffers.TryGetValue(node, out var existing))
            return existing;

        var buffer = _device.CreateBuffer(new BufferDescription
        {
            Size = 64,
            Usage = BufferUsage.Uniform | BufferUsage.CopyDst
        });
        _selectionMaskModelBuffers.Add(node, buffer);
        return buffer;
    }

    private IBindGroup GetSelectionMaskModelBindGroup(ModelNode node)
    {
        if (_selectionMaskModelBindGroups.TryGetValue(node, out var existing))
            return existing;

        var bindGroup = _selectionMaskPipeline.CreateBindGroup(1,
        [
            new BindGroupBinding { Slot = 0, Buffer = GetSelectionMaskModelBuffer(node), BufferSize = 64 }
        ]);
        _selectionMaskModelBindGroups.Add(node, bindGroup);
        return bindGroup;
    }

    /// <summary>Writes the outline color/size into the composite pass uniform.</summary>
    private void UpdateOutlineParams()
    {
        var parameters = new SelectionOutlineParams
        {
            Color = new Vector4(Outline.Color, Outline.Opacity),
            // The mask is viewport-sized now, so its texel is 1/viewport.
            TexelSize = new Vector2(1f / _sceneTargetWidth, 1f / _sceneTargetHeight),
            Thickness = Outline.Thickness
        };
        _outlineParamsBuffer.Write(in parameters);
    }

    /// <summary>
    /// Translates the backdrop regions collected by the tree-walk painter into
    /// the GPU parameter buffer (Ui/Backdrop.slang reads one struct per instance)
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
                // The scene texture is viewport-sized and blitted into the
                // viewport rect, so scene UVs are viewport-relative.
                UvRect = new Vector4(
                    (region.X - _sceneQuadRect.X) / _sceneQuadRect.Width,
                    (region.Y - _sceneQuadRect.Y) / _sceneQuadRect.Height,
                    (region.X + region.Width - _sceneQuadRect.X) / _sceneQuadRect.Width,
                    (region.Y + region.Height - _sceneQuadRect.Y) / _sceneQuadRect.Height),
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
                        // Op kinds match FilterOps.slang / FilterOpKind, so the
                        // 3D backdrop and 2D filter passes share one table.
                        "brightness" => (int)FilterOpKind.Brightness,
                        "contrast" => (int)FilterOpKind.Contrast,
                        "grayscale" => (int)FilterOpKind.Grayscale,
                        "hue-rotate" => (int)FilterOpKind.HueRotate,
                        "invert" => (int)FilterOpKind.Invert,
                        "opacity" => (int)FilterOpKind.Opacity,
                        "saturate" => (int)FilterOpKind.Saturate,
                        "sepia" => (int)FilterOpKind.Sepia,
                        _ => -1
                    };
                    if (type < 0) continue;
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
        _displayTexture?.Dispose();
        DisposePostProcessFrameResources();
        _postProcessTextureA?.Dispose();
        _postProcessTextureB?.Dispose();
        foreach (var scratch in _postProcessScratch)
            scratch.Dispose();
        _postProcessPointSampler?.Dispose();
        foreach (var pipeline in _postProcessPipelines.Values)
            pipeline.Dispose();
        _postProcessPipelines.Clear();
        _backdropBindGroup?.Dispose();
        _backdropParamsBuffer?.Dispose();
        _backdropPipeline?.Dispose();
        _uiVertexBuffer?.Dispose();
        _scenePipeline?.Dispose();
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
        foreach (var bindGroup in _cameraBindGroups.Values)
            bindGroup.Dispose();
        _cameraBindGroups.Clear();
        foreach (var bindGroup in _environmentBindGroups.Values)
            bindGroup.Dispose();
        _environmentBindGroups.Clear();
        foreach (var pipeline in _meshPipelines.Values)
            pipeline.Dispose();
        _meshPipelines.Clear();
        foreach (var texture in _materialTextures.Values)
            texture.Dispose();
        _materialTextures.Clear();
        foreach (var shadowRenderable in _shadowRenderables.Values)
        {
            shadowRenderable.BindGroup.Dispose();
            shadowRenderable.ModelBuffer.Dispose();
        }
        _shadowRenderables.Clear();
        foreach (var bindGroup in _selectionMaskModelBindGroups.Values)
            bindGroup.Dispose();
        _selectionMaskModelBindGroups.Clear();
        foreach (var buffer in _selectionMaskModelBuffers.Values)
            buffer.Dispose();
        _selectionMaskModelBuffers.Clear();
        if (_shadowViewProjBindGroups is not null)
        {
            foreach (var bindGroup in _shadowViewProjBindGroups)
                bindGroup?.Dispose();
        }
        if (_shadowViewProjBuffers is not null)
        {
            foreach (var buffer in _shadowViewProjBuffers)
                buffer?.Dispose();
        }
        _shadowPipeline?.Dispose();
        _shadowDataBuffer?.Dispose();
        _shadowSampler?.Dispose();
        _shadowAtlas?.Dispose();
        _defaultWhiteTexture?.Dispose();
        _defaultBlackTexture?.Dispose();
        _defaultNormalTexture?.Dispose();
        _defaultEnvironmentCube?.Dispose();
        _defaultIrradianceCube?.Dispose();
        _defaultPrefilteredCube?.Dispose();
        _defaultBrdfLut?.Dispose();
        _environmentSampler?.Dispose();
        _environmentUniformBuffer?.Dispose();
        _environmentPreprocessor.Dispose();
        _skyCameraBindGroup?.Dispose();
        _skyPlaceholderBindGroup?.Dispose();
        _skyPipeline?.Dispose();
        _materialSampler?.Dispose();
        _lightsBuffer?.Dispose();
        _cameraBuffer?.Dispose();
        _gridBindGroup?.Dispose();
        _gridUniformBuffer?.Dispose();
        _gridVertexBuffer?.Dispose();
        _gridPipeline?.Dispose();
        Gizmos?.Dispose();
        _outlineBindGroup?.Dispose();
        _outlineParamsBuffer?.Dispose();
        _outlinePipeline?.Dispose();
        _selectionMaskSceneBindGroup?.Dispose();
        _selectionMaskPipeline?.Dispose();
        _selectionTexture?.Dispose();
        _sceneDepth?.Dispose();
        _surfaceDepth?.Dispose();
        _sceneQuadVertexBuffer?.Dispose();
    }
}
