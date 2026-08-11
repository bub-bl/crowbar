using System.Numerics;
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
        Ring = 2
    }

    // Mirrors GizmoSpriteParams in Shaders/GizmoSprite.wgsl.
    [StructLayout(LayoutKind.Sequential)]
    private struct GizmoSpriteParams
    {
        public Vector4 Center;
        public Vector4 Color;
        public Vector4 ScaleKind; // x = half-size (world), y = kind
    }

    // Mirrors GizmoWidgetElement in Shaders/GizmoLine.wgsl.
    [StructLayout(LayoutKind.Sequential)]
    private struct GizmoWidgetElement
    {
        public Vector4 Start;    // xyz = world start (widget origin)
        public Vector4 End;      // xyz = world end (shaft tip / head apex)
        public Vector4 Color;
        public Vector4 Sizes;    // x = kind (0 shaft, 1 head), y = shaft half-width (px),
                                 // z = head length (px), w = head half-width (px)
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
    // on-screen thickness, and the tips are arrowhead triangles.
    private const float ShaftHalfWidthPx = 3f;    // 6px thick shafts
    private const float HeadLengthPx = 26f;       // arrowhead length
    private const float HeadHalfWidthPx = 10f;    // arrowhead half-width (20px)

    private readonly IGraphicsDevice _device;
    private readonly IBuffer _sceneBuffer;
    private readonly ulong _sceneBufferSize;
    private readonly GizmoSpriteParams[] _spriteParams = new GizmoSpriteParams[MaxSprites];
    private readonly GizmoWidgetElement[] _widgetElements = new GizmoWidgetElement[6];
    private IPipeline _widgetPipeline = null!;
    private IPipeline _spritePipeline = null!;
    private IBuffer _shaftVertexBuffer = null!;
    private IBuffer _headVertexBuffer = null!;
    private IBuffer _spriteVertexBuffer = null!;
    private IBuffer _widgetElementsBuffer = null!;
    private IBuffer _spriteParamsBuffer = null!;
    private IBindGroup _widgetBindGroup = null!;
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
    public void UpdateInteraction(Camera camera, Vector2 mousePixels, bool mouseDown, int width, int height)
    {
        if (!Enabled)
            return;

        Gizmo.SetTarget(Selection?.GetComponent<TransformComponent>());
        if (Gizmo.Target is null)
            return;

        var aspect = Math.Max(1, width) / (float)Math.Max(1, height);
        var view = camera.ViewMatrix;
        var projection = camera.ProjectionMatrix(aspect);
        var ray = Ray.FromScreen(mousePixels, width, height, view, projection);

        var pressed = mouseDown && !_wasMouseDown;
        var released = !mouseDown && _wasMouseDown;
        _wasMouseDown = mouseDown;

        if (released)
            Gizmo.EndDrag();

        if (pressed)
        {
            Gizmo.UpdateHover(ray, mousePixels, view, projection, width, height);
            if (Gizmo.HoveredAxis != TranslationGizmo.Axis.None)
                Gizmo.BeginDrag(ray);
        }
        else if (mouseDown && Gizmo.IsDragging)
        {
            Gizmo.Drag(ray);
        }
        else
        {
            Gizmo.UpdateHover(ray, mousePixels, view, projection, width, height);
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

            // Thick shafts (quad per axis) then arrowheads (triangle per axis),
            // colored by axis state — the shader does no state coloring itself.
            var elementCount = 0;
            for (var axis = TranslationGizmo.Axis.X; axis <= TranslationGizmo.Axis.Z; axis++)
            {
                var tip = origin + Gizmo.AxisDirection(axis) * widgetScale;
                _widgetElements[elementCount++] = new GizmoWidgetElement
                {
                    Start = new Vector4(origin, 1f),
                    End = new Vector4(tip, 1f),
                    Color = AxisStateColor(axis, hovered, active),
                    Sizes = new Vector4(0f, ShaftHalfWidthPx, HeadLengthPx, HeadHalfWidthPx),
                    Viewport = new Vector4(width, height, 0f, 0f)
                };
            }
            for (var axis = TranslationGizmo.Axis.X; axis <= TranslationGizmo.Axis.Z; axis++)
            {
                var tip = origin + Gizmo.AxisDirection(axis) * widgetScale;
                _widgetElements[elementCount++] = new GizmoWidgetElement
                {
                    Start = new Vector4(origin, 1f),
                    End = new Vector4(tip, 1f),
                    Color = AxisStateColor(axis, hovered, active),
                    Sizes = new Vector4(1f, ShaftHalfWidthPx, HeadLengthPx, HeadHalfWidthPx),
                    Viewport = new Vector4(width, height, 0f, 0f)
                };
            }

            var bytes = new byte[elementCount * sizeof(GizmoWidgetElement)];
            unsafe
            {
                fixed (byte* destination = bytes)
                {
                    var elements = (GizmoWidgetElement*)destination;
                    for (var i = 0; i < elementCount; i++)
                        elements[i] = _widgetElements[i];
                }
            }
            _widgetElementsBuffer.Write(bytes);

            pass.SetPipeline(_widgetPipeline);
            pass.SetBindGroup(_widgetBindGroup, 0);
            pass.SetVertexBuffer(_shaftVertexBuffer, _shaftVertexBuffer.Size);
            pass.DrawInstanced(6, 3);
            pass.SetVertexBuffer(_headVertexBuffer, _headVertexBuffer.Size);
            pass.DrawInstanced(3, 3);
        }

        if (world is not null && ShowLightSprites)
        {
            foreach (var light in world.Query<Light>())
            {
                if (!light.Enabled)
                    continue;
                var position = light.World.Position;
                AddSprite(ref spriteCount, position, new Vector4(light.Color, 0.95f),
                    screenHalfSize(position, SpritePixelSize * 0.5f), SpriteKind.Circle);
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
    /// Picks the nearest mesh renderer under the mouse through its world AABB
    /// (CPU ray cast, like the grid's unproject but in reverse).
    /// </summary>
    public Entity? Pick(World? world, Camera camera, Vector2 mousePixels, int width, int height)
    {
        if (world is null)
            return null;

        var ray = Ray.FromScreen(mousePixels, width, height, camera);
        Entity? best = null;
        var bestDistance = float.MaxValue;

        foreach (var renderer in world.Query<MeshRenderer>())
        {
            if (!renderer.IsValid || renderer.Model is null)
                continue;

            var worldBounds = renderer.Model.Bounds.TransformBy(ToWorldMatrix(renderer.World));
            if (ray.Intersects(in worldBounds, out var distance) && distance < bestDistance)
            {
                bestDistance = distance;
                best = renderer.Entity;
            }
        }

        return best;
    }

    private void AddSprite(ref int count, Vector3 position, Vector4 color, float halfSize, SpriteKind kind)
    {
        if (count >= MaxSprites)
            return;
        _spriteParams[count++] = new GizmoSpriteParams
        {
            Center = new Vector4(position, 1f),
            Color = color,
            ScaleKind = new Vector4(halfSize, (float)kind, 0f, 0f)
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
            0f, -1f,  1f, -1f,  0f, 1f,
            1f, -1f,  1f, 1f,   0f, 1f
        ];
        _shaftVertexBuffer = CreateBuffer((ulong)(shaftVertices.Length * sizeof(float)), BufferUsage.Vertex | BufferUsage.CopyDst);
        unsafe
        {
            fixed (float* data = shaftVertices)
                _shaftVertexBuffer.Write(new ReadOnlySpan<byte>(data, shaftVertices.Length * sizeof(float)));
        }

        // Arrowhead triangle: apex (0, 0) + two base corners (1, ±1).
        float[] headVertices = [0f, 0f,  1f, -1f,  1f, 1f];
        _headVertexBuffer = CreateBuffer((ulong)(headVertices.Length * sizeof(float)), BufferUsage.Vertex | BufferUsage.CopyDst);
        unsafe
        {
            fixed (float* data = headVertices)
                _headVertexBuffer.Write(new ReadOnlySpan<byte>(data, headVertices.Length * sizeof(float)));
        }

        // Billboard quad: six corner vertices in -1..1.
        float[] quad = [-1f, -1f, 1f, -1f, 1f, 1f, -1f, -1f, 1f, 1f, -1f, 1f];
        _spriteVertexBuffer = CreateBuffer((ulong)(quad.Length * sizeof(float)), BufferUsage.Vertex | BufferUsage.CopyDst);
        unsafe
        {
            fixed (float* data = quad)
                _spriteVertexBuffer.Write(new ReadOnlySpan<byte>(data, quad.Length * sizeof(float)));
        }

        // 3 shafts + 3 arrowheads, written every frame from the gizmo state.
        _widgetElementsBuffer = CreateBuffer((ulong)(6 * sizeof(GizmoWidgetElement)), BufferUsage.Storage | BufferUsage.CopyDst);
        _spriteParamsBuffer = CreateBuffer((ulong)(MaxSprites * sizeof(GizmoSpriteParams)), BufferUsage.Storage | BufferUsage.CopyDst);

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
                    new BindGroupLayoutBinding { Slot = 1, Type = BindingType.ReadOnlyStorageBuffer, Stages = ShaderStage.Vertex | ShaderStage.Fragment }
                ]
            ]
        });
        _widgetBindGroup = _widgetPipeline.CreateBindGroup(
        [
            new BindGroupBinding { Slot = 0, Buffer = _sceneBuffer, BufferSize = _sceneBufferSize },
            new BindGroupBinding { Slot = 1, Buffer = _widgetElementsBuffer, BufferSize = (ulong)(6 * sizeof(GizmoWidgetElement)) }
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
                    new BindGroupLayoutBinding { Slot = 1, Type = BindingType.ReadOnlyStorageBuffer, Stages = ShaderStage.Vertex | ShaderStage.Fragment }
                ]
            ]
        });
        _spriteBindGroup = _spritePipeline.CreateBindGroup(
        [
            new BindGroupBinding { Slot = 0, Buffer = _sceneBuffer, BufferSize = _sceneBufferSize },
            new BindGroupBinding { Slot = 1, Buffer = _spriteParamsBuffer, BufferSize = (ulong)(MaxSprites * sizeof(GizmoSpriteParams)) }
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

        _widgetBindGroup?.Dispose();
        _spriteBindGroup?.Dispose();
        _widgetElementsBuffer?.Dispose();
        _spriteParamsBuffer?.Dispose();
        _shaftVertexBuffer?.Dispose();
        _headVertexBuffer?.Dispose();
        _spriteVertexBuffer?.Dispose();
        _widgetPipeline?.Dispose();
        _spritePipeline?.Dispose();
    }
}
