using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Crowbar.Engine.Rendering;

/// <summary>
/// Viewport gizmo pass: the translation widget around the selected entity and
/// camera-facing billboard sprites (light icons, selection ring). It is a
/// pure overlay — nothing here writes to the world; interaction moves the
/// selected entity's transform through <see cref="TranslationGizmo"/>. Drawn
/// after the grid, always on top of the scene, and disabled in game.
/// </summary>
public sealed class GizmoRenderer : IDisposable
{
    public const int MaxSprites = 128;

    private enum SpriteKind
    {
        Circle = 0,
        Diamond = 1,
        Ring = 2,
        Icon = 3
    }

    // Mirrors GizmoSpriteParams in Shaders/GizmoSprite.wgsl.
    [StructLayout(LayoutKind.Sequential)]
    private struct GizmoSpriteParams
    {
        public Vector4 Center;
        public Vector4 Color;
        public Vector4 ScaleKind; // x = half-size (world), y = kind
        public Vector4 UvRect;    // icon atlas rectangle (u0, v0, u1, v1)
    }

    // Mirrors GizmoWidgetElement in Shaders/GizmoLine.wgsl.
    [StructLayout(LayoutKind.Sequential)]
    private struct GizmoWidgetElement
    {
        public Vector4 Start;    // xyz = world start (widget origin)
        public Vector4 End;      // xyz = world end (shaft tip / head apex)
        public Vector4 Color;
        public Vector4 Sizes;    // shaft: x = kind (0), y = half-width (px);
                                 // cone: x = kind (1), z = length (world), w = radius (world)
        public Vector4 Viewport; // x = width, y = height in pixels (vec4 keeps the
                                 // element stride a multiple of 16)
    }

    private static readonly Vector3[] AxisColors =
    [
        new(1f, 0.25f, 0.25f), // X red
        new(0.25f, 1f, 0.25f), // Y green
        new(0.25f, 0.4f, 1f)   // Z blue
    ];

    private static readonly Vector4 SelectionColor = new(1f, 0.72f, 0.08f, 1f);
    private static readonly Vector4 ActiveAxisColor = new(1f, 0.85f, 0.1f, 1f);
    private static readonly Vector4 HoveredAxisColor = new(1f, 1f, 1f, 1f);

    // WebGPU has no wide lines, so the shafts are quads expanded to a constant
    // on-screen thickness, and the tips are true 3D cones.
    private const float ShaftHalfWidthPx = 3f;    // 6px thick shafts
    private const float HeadLengthPx = 26f;       // cone length in pixels
    private const float HeadHalfWidthPx = 10f;    // cone radius (20px diameter)
    private const int ConeVertexCount = 16 * 3;  // 16-sided cone, one triangle per side

    private readonly IGraphicsDevice _device;
    private readonly IBuffer _sceneBuffer;
    private readonly ulong _sceneBufferSize;
    private GizmoIconAtlas _iconAtlas = null!;
    private readonly GizmoSpriteParams[] _spriteParams = new GizmoSpriteParams[MaxSprites];
    private readonly GizmoWidgetElement[] _widgetElements = new GizmoWidgetElement[6];
    private IPipeline _widgetPipeline = null!;
    private IPipeline _spritePipeline = null!;
    private IBuffer _shaftVertexBuffer = null!;
    private IBuffer _coneVertexBuffer = null!;
    private IBuffer _spriteVertexBuffer = null!;
    private IBuffer _shaftElementsBuffer = null!;
    private IBuffer _coneElementsBuffer = null!;
    private IBuffer _spriteParamsBuffer = null!;
    private IBindGroup _shaftBindGroup = null!;
    private IBindGroup _coneBindGroup = null!;
    private IBindGroup _spriteBindGroup = null!;
    private bool _wasMouseDown;
    private bool _disposed;

    public GizmoRenderer(IGraphicsDevice device, IBuffer sceneBuffer, ulong sceneBufferSize)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _sceneBuffer = sceneBuffer ?? throw new ArgumentNullException(nameof(sceneBuffer));
        _sceneBufferSize = sceneBufferSize;
        CreateResources();
    }

    /// <summary>Widget logic (hover/drag/snap). Configure <see cref="TranslationGizmo.SnapSize"/> through <see cref="SnapSize"/>.</summary>
    public TranslationGizmo Gizmo { get; } = new();

    /// <summary>The entity the widget follows; null hides the widget.</summary>
    public Entity? Selection { get; set; }

    /// <summary>Whether the pass draws anything (off = pure game viewport).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Whether lights in the world get billboard icons.</summary>
    public bool ShowLightSprites { get; set; } = true;

    /// <summary>Whether non-selected mesh entities get billboard icons.</summary>
    public bool ShowEntitySprites { get; set; } = true;

    /// <summary>Snap size for dragged movement in world units, or null to move freely.</summary>
    public float? SnapSize
    {
        get => Gizmo.SnapSize;
        set => Gizmo.SnapSize = value;
    }

    /// <summary>Screen-constant light icon size in pixels (diameter).</summary>
    public float SpritePixelSize { get; set; } = 26f;

    /// <summary>Screen-constant widget shaft length in pixels.</summary>
    public float WidgetPixelSize { get; set; } = 100f;

    /// <summary>
    /// Advances the gizmo from mouse input: hover follows the cursor, a press
    /// on a hovered axis starts a drag, movement drags the selected entity
    /// along the axis, release ends it.
    /// </summary>
    public void UpdateInteraction(CameraMatrices matrices, Vector2 mousePixels, bool mouseDown)
    {
        if (!Enabled)
            return;

        Gizmo.SetTarget(Selection?.GetComponent<TransformComponent>());
        if (Gizmo.Target is null)
            return;

        var ray = matrices.RayFromScreen(mousePixels);

        var pressed = mouseDown && !_wasMouseDown;
        var released = !mouseDown && _wasMouseDown;
        _wasMouseDown = mouseDown;

        if (released)
            Gizmo.EndDrag();

        if (pressed)
        {
            Gizmo.UpdateHover(ray, mousePixels, matrices.View, matrices.Projection, matrices.Width, matrices.Height);
            if (Gizmo.HoveredAxis != TranslationGizmo.Axis.None)
                Gizmo.BeginDrag(ray);
        }
        else if (mouseDown && Gizmo.IsDragging)
        {
            Gizmo.Drag(ray);
        }
        else
        {
            Gizmo.UpdateHover(ray, mousePixels, matrices.View, matrices.Projection, matrices.Width, matrices.Height);
        }
    }

    /// <summary>Draws the widget and the billboard sprites on top of the scene.</summary>
    public void Draw(IRenderPass pass, World? world, Camera camera, int width, int height)
    {
        if (_disposed || !Enabled)
            return;

        var target = Selection?.GetComponent<TransformComponent>();
        Gizmo.SetTarget(target);

        if (target is null && (world is null || !ShowLightSprites))
            return;

        var tanHalfFov = MathF.Tan(camera.FieldOfView * 0.5f);
        // World half-size of an object that must span `pixels` on screen at
        // `position`: pixels = size * height / (2 * depth * tan(fov/2)), where
        // depth is the distance along the camera's forward axis (so the size
        // is exact at any viewing angle).
        float screenHalfSize(Vector3 position, float pixels)
        {
            var depth = Vector3.Dot(camera.Forward, position - camera.Position);
            return pixels * 2f * Math.Max(1e-4f, depth) * tanHalfFov / Math.Max(1, height);
        }

        float screenWorldSize(float pixels, float depth) =>
            pixels * 2f * Math.Max(1e-4f, depth) * tanHalfFov / Math.Max(1, height);

        var spriteCount = 0;

        if (target is not null)
        {
            var origin = Gizmo.Origin;
            var originDepth = Math.Max(1e-4f, Vector3.Dot(camera.Forward, origin - camera.Position));
            var widgetScale = WidgetPixelSize * 2f * originDepth * tanHalfFov
                              / (Math.Max(1, height) * Gizmo.AxisLength);
            var hovered = (float)Gizmo.HoveredAxis;
            var active = (float)Gizmo.ActiveAxis;

            // Selection ring around the widget origin.
            AddSprite(ref spriteCount, origin, SelectionColor, screenHalfSize(origin, 28f), SpriteKind.Ring);

            // Thick shafts (screen-space quads) and true 3D cone heads, colored
            // by axis state. The first three elements are shafts; the last three
            // are cones and are uploaded to separate storage buffers below.
            for (var axis = TranslationGizmo.Axis.X; axis <= TranslationGizmo.Axis.Z; axis++)
            {
                var direction = Gizmo.AxisDirection(axis);
                var tip = origin + direction * widgetScale;
                var tipDepth = Math.Max(1e-4f, Vector3.Dot(camera.Forward, tip - camera.Position));
                var coneLength = screenWorldSize(HeadLengthPx, tipDepth);
                var shaftTip = tip - direction * coneLength;
                _widgetElements[(int)axis] = new GizmoWidgetElement
                {
                    Start = new Vector4(origin, 1f),
                    End = new Vector4(shaftTip, 1f),
                    Color = AxisStateColor(axis, hovered, active),
                    Sizes = new Vector4(0f, ShaftHalfWidthPx, 0f, 0f),
                    Viewport = new Vector4(width, height, 0f, 0f)
                };
            }
            for (var axis = TranslationGizmo.Axis.X; axis <= TranslationGizmo.Axis.Z; axis++)
            {
                var tip = origin + Gizmo.AxisDirection(axis) * widgetScale;
                var tipDepth = Math.Max(1e-4f, Vector3.Dot(camera.Forward, tip - camera.Position));
                var coneLength = screenWorldSize(HeadLengthPx, tipDepth);
                var coneRadius = screenWorldSize(HeadHalfWidthPx, tipDepth);
                _widgetElements[(int)axis + 3] = new GizmoWidgetElement
                {
                    Start = new Vector4(origin, 1f),
                    End = new Vector4(tip, 1f),
                    Color = AxisStateColor(axis, hovered, active),
                    Sizes = new Vector4(1f, 0f, coneLength, coneRadius),
                    Viewport = new Vector4(width, height, 0f, 0f)
                };
            }

            // Queue writes happen before command execution, so separate buffers
            // ensure the shaft draw cannot observe the cone upload.
            WriteWidgetElements(_shaftElementsBuffer, 0, 3);
            WriteWidgetElements(_coneElementsBuffer, 3, 3);

            pass.SetPipeline(_widgetPipeline);
            pass.SetBindGroup(_shaftBindGroup, 0);
            pass.SetVertexBuffer(_shaftVertexBuffer, _shaftVertexBuffer.Size);
            pass.DrawInstanced(6, 3);
            pass.SetBindGroup(_coneBindGroup, 0);
            pass.SetVertexBuffer(_coneVertexBuffer, _coneVertexBuffer.Size);
            pass.DrawInstanced((uint)ConeVertexCount, 3);
        }

        if (world is not null && ShowLightSprites)
        {
            foreach (var light in world.Query<Light>())
            {
                if (!light.Enabled)
                    continue;

                var lightIcon = ResolveIcon(light);
                if (lightIcon is null)
                    continue;

                var position = light.World.Position;
                AddIconSprite(ref spriteCount, position, new Vector4(light.Color, 0.95f),
                    screenHalfSize(position, SpritePixelSize * 0.5f), lightIcon.Value);
            }
        }

        if (world is not null && ShowEntitySprites)
        {
            foreach (var meshRenderer in world.Query<MeshRenderer>())
            {
                if (!meshRenderer.IsValid || meshRenderer.Model is null || meshRenderer.Entity == Selection)
                    continue;

                var meshIcon = ResolveIcon(meshRenderer);
                if (meshIcon is null)
                    continue;

                var position = meshRenderer.World.Position;
                AddIconSprite(ref spriteCount, position, new Vector4(1f, 1f, 1f, 0.85f),
                    screenHalfSize(position, SpritePixelSize * 0.5f), meshIcon.Value);
            }
        }

        if (spriteCount == 0)
            return;

        var spriteBytes = new byte[spriteCount * sizeof(GizmoSpriteParams)];
        unsafe
        {
            fixed (byte* destination = spriteBytes)
            {
                var sprites = (GizmoSpriteParams*)destination;
                for (var i = 0; i < spriteCount; i++)
                    sprites[i] = _spriteParams[i];
            }
        }
        _spriteParamsBuffer.Write(spriteBytes);

        pass.SetPipeline(_spritePipeline);
        pass.SetBindGroup(_spriteBindGroup, 0);
        pass.SetVertexBuffer(_spriteVertexBuffer, _spriteVertexBuffer.Size);
        pass.DrawInstanced(6, (uint)spriteCount);
    }

    /// <summary>
    /// Picks a visible light icon by its screen-space billboard first, then
    /// falls back to mesh AABB picking. Light icons are an overlay, so they
    /// must win over a mesh that happens to be behind the icon.
    /// </summary>
    public Entity? Pick(World? world, CameraMatrices matrices, Vector2 mousePixels)
    {
        if (world is null)
            return null;

        var view = matrices.View;
        var projection = matrices.Projection;
        var width = matrices.Width;
        var height = matrices.Height;
        Entity? bestLight = null;
        var bestLightDepth = float.MaxValue;
        var iconRadius = SpritePixelSize * 0.75f;

        if (ShowLightSprites)
        {
            foreach (var light in world.Query<Light>())
            {
                if (!light.Enabled || !TryProjectToScreen(light.World.Position, view, projection, width, height,
                        out var screenPosition, out var depth))
                    continue;

                if (Vector2.Distance(mousePixels, screenPosition) <= iconRadius && depth < bestLightDepth)
                {
                    bestLight = light.Entity;
                    bestLightDepth = depth;
                }
            }
        }

        if (bestLight is not null)
            return bestLight;

        var ray = matrices.RayFromScreen(mousePixels);
        Entity? bestMesh = null;
        var bestDistance = float.MaxValue;

        foreach (var renderer in world.Query<MeshRenderer>())
        {
            if (!renderer.IsValid || renderer.Model is null)
                continue;

            var worldBounds = renderer.Model.Bounds.TransformBy(ToWorldMatrix(renderer.World));
            if (ray.Intersects(in worldBounds, out var distance) && distance < bestDistance)
            {
                bestDistance = distance;
                bestMesh = renderer.Entity;
            }
        }

        return bestMesh;
    }

    private static bool TryProjectToScreen(Vector3 worldPosition, Matrix4x4 view, Matrix4x4 projection,
        int width, int height, out Vector2 screenPosition, out float depth)
    {
        var clip = Vector4.Transform(new Vector4(worldPosition, 1f), view * projection);
        if (clip.W <= 1e-6f)
        {
            screenPosition = default;
            depth = float.MaxValue;
            return false;
        }

        var ndc = new Vector2(clip.X / clip.W, clip.Y / clip.W);
        screenPosition = new Vector2(
            (ndc.X * 0.5f + 0.5f) * width,
            (1f - (ndc.Y * 0.5f + 0.5f)) * height);
        depth = clip.W;
        return true;
    }

    private void WriteWidgetElements(IBuffer buffer, int start, int count)
    {
        var bytes = new byte[count * sizeof(GizmoWidgetElement)];
        unsafe
        {
            fixed (byte* destination = bytes)
            {
                var elements = (GizmoWidgetElement*)destination;
                for (var i = 0; i < count; i++)
                    elements[i] = _widgetElements[start + i];
            }
        }
        buffer.Write(bytes);
    }

    private void AddSprite(ref int count, Vector3 position, Vector4 color, float halfSize, SpriteKind kind)
    {
        if (count >= MaxSprites)
            return;
        _spriteParams[count++] = new GizmoSpriteParams
        {
            Center = new Vector4(position, 1f),
            Color = color,
            ScaleKind = new Vector4(halfSize, (float)kind, 0f, 0f),
            UvRect = Vector4.Zero
        };
    }

    /// <summary>
    /// Resolves the gizmo icon a component declares through
    /// <see cref="GizmoIconAttribute"/>. Null when the component has no
    /// attribute or the icon is not in the atlas.
    /// </summary>
    private GizmoIcon? ResolveIcon(Component component)
    {
        var attribute = component.GetType().GetCustomAttribute<GizmoIconAttribute>();
        if (attribute is null || !_iconAtlas.Contains(new GizmoIcon(attribute.Name)))
            return null;

        return new GizmoIcon(attribute.Name);
    }

    private void AddIconSprite(ref int count, Vector3 position, Vector4 color, float halfSize, GizmoIcon icon)
    {
        if (count >= MaxSprites)
            return;

        _spriteParams[count++] = new GizmoSpriteParams
        {
            Center = new Vector4(position, 1f),
            Color = color,
            ScaleKind = new Vector4(halfSize, (float)SpriteKind.Icon, 0f, 0f),
            UvRect = _iconAtlas.GetUv(icon)
        };
    }

    private static Vector4 AxisStateColor(TranslationGizmo.Axis axis, float hovered, float active)
    {
        var axisIndex = (float)axis;
        if (active >= 0f && active == axisIndex)
            return ActiveAxisColor;
        if (hovered >= 0f && hovered == axisIndex)
            return HoveredAxisColor;
        if (active >= 0f)
            return new Vector4(AxisColors[(int)axis], 0.35f);
        return new Vector4(AxisColors[(int)axis], 1f);
    }

    private static Matrix4x4 ToWorldMatrix(Transform transform) =>
        Matrix4x4.CreateScale(transform.Scale)
        * Matrix4x4.CreateFromQuaternion(transform.Rotation.Quaternion)
        * Matrix4x4.CreateTranslation(transform.Position);

    private void CreateResources()
    {
        // Shaft quad in uv space: x = 0 at the origin, 1 at the tip; y = -1/+1
        // across. The vertex shader expands it to a thick screen-space quad.
        float[] shaftVertices =
        [
            0f, -1f, 0f, 0f,  1f, -1f, 0f, 0f,  0f, 1f, 0f, 0f,
            1f, -1f, 0f, 0f,  1f, 1f, 0f, 0f,   0f, 1f, 0f, 0f
        ];
        _shaftVertexBuffer = CreateBuffer((ulong)(shaftVertices.Length * sizeof(float)), BufferUsage.Vertex | BufferUsage.CopyDst);
        unsafe
        {
            fixed (float* data = shaftVertices)
                _shaftVertexBuffer.Write(new ReadOnlySpan<byte>(data, shaftVertices.Length * sizeof(float)));
        }

        // Cone surface: each segment is one triangle (two base-ring vertices
        // plus the apex). The shader transforms this unit cone onto the gizmo
        // axis and applies its world-space length/radius.
        const int coneSegments = 16;
        const int coneVertexCount = coneSegments * 3;
        var coneVertices = new float[coneVertexCount * 4];
        for (var segment = 0; segment < coneSegments; segment++)
        {
            var angle0 = segment * MathF.Tau / coneSegments;
            var angle1 = (segment + 1) * MathF.Tau / coneSegments;
            var offset = segment * 3 * 4;
            coneVertices[offset] = MathF.Cos(angle0);
            coneVertices[offset + 1] = MathF.Sin(angle0);
            coneVertices[offset + 2] = 0f;
            coneVertices[offset + 4] = MathF.Cos(angle1);
            coneVertices[offset + 5] = MathF.Sin(angle1);
            coneVertices[offset + 6] = 0f;
            coneVertices[offset + 8] = 0f;
            coneVertices[offset + 9] = 0f;
            coneVertices[offset + 10] = 1f;
        }
        _coneVertexBuffer = CreateBuffer((ulong)(coneVertices.Length * sizeof(float)), BufferUsage.Vertex | BufferUsage.CopyDst);
        unsafe
        {
            fixed (float* data = coneVertices)
                _coneVertexBuffer.Write(new ReadOnlySpan<byte>(data, coneVertices.Length * sizeof(float)));
        }

        // Billboard quad: six corner vertices in -1..1.
        float[] quad = [-1f, -1f, 1f, -1f, 1f, 1f, -1f, -1f, 1f, 1f, -1f, 1f];
        _spriteVertexBuffer = CreateBuffer((ulong)(quad.Length * sizeof(float)), BufferUsage.Vertex | BufferUsage.CopyDst);
        unsafe
        {
            fixed (float* data = quad)
                _spriteVertexBuffer.Write(new ReadOnlySpan<byte>(data, quad.Length * sizeof(float)));
        }

        // Three shaft elements and three cone elements are written every frame
        // from the gizmo state into separate buffers.
        _shaftElementsBuffer = CreateBuffer((ulong)(3 * sizeof(GizmoWidgetElement)), BufferUsage.Storage | BufferUsage.CopyDst);
        _coneElementsBuffer = CreateBuffer((ulong)(3 * sizeof(GizmoWidgetElement)), BufferUsage.Storage | BufferUsage.CopyDst);
        _spriteParamsBuffer = CreateBuffer((ulong)(MaxSprites * sizeof(GizmoSpriteParams)), BufferUsage.Storage | BufferUsage.CopyDst);
        _iconAtlas = GizmoIconAtlas.Load(_device);

        // Both gizmo shaders #include Common/Transform.wgsl: load through
        // Shader.Load so the preprocessor flattens the includes.
        string ReadShader(string name) => Shader.Load(Path.Combine("Shaders", name)).Source;

        _widgetPipeline = _device.CreatePipeline(new PipelineDescription
        {
            ShaderSource = ReadShader("GizmoLine.wgsl"),
            VertexEntryPoint = "vs_main",
            FragmentEntryPoint = "fs_main",
            ColorFormat = _device.Swapchain.Format,
            DepthFormat = TextureFormat.Depth24Plus,
            DepthCompare = CompareFunction.Always,
            DepthWriteEnabled = false,
            VertexLayout = new VertexBufferLayoutDescription
            {
                Stride = 4 * sizeof(float),
                Attributes =
                [
                    new VertexAttributeDescription { Format = VertexFormat.Float32x4, Offset = 0, ShaderLocation = 0 }
                ]
            },
            BindGroups =
            [
                [
                    new BindGroupLayoutBinding { Slot = 0, Type = BindingType.UniformBuffer, Stages = ShaderStage.Vertex | ShaderStage.Fragment },
                    new BindGroupLayoutBinding { Slot = 1, Type = BindingType.ReadOnlyStorageBuffer, Stages = ShaderStage.Vertex | ShaderStage.Fragment }
                ]
            ]
        });
        _shaftBindGroup = _widgetPipeline.CreateBindGroup(
        [
            new BindGroupBinding { Slot = 0, Buffer = _sceneBuffer, BufferSize = _sceneBufferSize },
            new BindGroupBinding { Slot = 1, Buffer = _shaftElementsBuffer, BufferSize = (ulong)(3 * sizeof(GizmoWidgetElement)) }
        ]);
        _coneBindGroup = _widgetPipeline.CreateBindGroup(
        [
            new BindGroupBinding { Slot = 0, Buffer = _sceneBuffer, BufferSize = _sceneBufferSize },
            new BindGroupBinding { Slot = 1, Buffer = _coneElementsBuffer, BufferSize = (ulong)(3 * sizeof(GizmoWidgetElement)) }
        ]);

        _spritePipeline = _device.CreatePipeline(new PipelineDescription
        {
            ShaderSource = ReadShader("GizmoSprite.wgsl"),
            VertexEntryPoint = "vs_main",
            FragmentEntryPoint = "fs_main",
            ColorFormat = _device.Swapchain.Format,
            DepthFormat = TextureFormat.Depth24Plus,
            AlphaBlend = true,
            DepthCompare = CompareFunction.Always,
            DepthWriteEnabled = false,
            VertexLayout = new VertexBufferLayoutDescription
            {
                Stride = 2 * sizeof(float),
                Attributes =
                [
                    new VertexAttributeDescription { Format = VertexFormat.Float32x2, Offset = 0, ShaderLocation = 0 }
                ]
            },
            BindGroups =
            [
                [
                    new BindGroupLayoutBinding { Slot = 0, Type = BindingType.UniformBuffer, Stages = ShaderStage.Vertex | ShaderStage.Fragment },
                    new BindGroupLayoutBinding { Slot = 1, Type = BindingType.ReadOnlyStorageBuffer, Stages = ShaderStage.Vertex | ShaderStage.Fragment },
                    new BindGroupLayoutBinding { Slot = 2, Type = BindingType.Texture, Stages = ShaderStage.Fragment },
                    new BindGroupLayoutBinding { Slot = 3, Type = BindingType.Sampler, Stages = ShaderStage.Fragment }
                ]
            ]
        });
        _spriteBindGroup = _spritePipeline.CreateBindGroup(
        [
            new BindGroupBinding { Slot = 0, Buffer = _sceneBuffer, BufferSize = _sceneBufferSize },
            new BindGroupBinding { Slot = 1, Buffer = _spriteParamsBuffer, BufferSize = (ulong)(MaxSprites * sizeof(GizmoSpriteParams)) },
            new BindGroupBinding { Slot = 2, Texture = _iconAtlas.Texture },
            new BindGroupBinding { Slot = 3, Sampler = _iconAtlas.Sampler }
        ]);
    }

    private IBuffer CreateBuffer(ulong size, BufferUsage usage) => _device.CreateBuffer(new BufferDescription
    {
        Size = size,
        Usage = usage
    });

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _shaftBindGroup?.Dispose();
        _coneBindGroup?.Dispose();
        _spriteBindGroup?.Dispose();
        _shaftElementsBuffer?.Dispose();
        _coneElementsBuffer?.Dispose();
        _spriteParamsBuffer?.Dispose();
        _shaftVertexBuffer?.Dispose();
        _coneVertexBuffer?.Dispose();
        _spriteVertexBuffer?.Dispose();
        _iconAtlas?.Dispose();
        _widgetPipeline?.Dispose();
        _spritePipeline?.Dispose();
    }
}
