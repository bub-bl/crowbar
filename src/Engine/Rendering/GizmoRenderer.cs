using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using Crowbar.FileSystems;

namespace Crowbar.Engine.Rendering;

/// <summary>
/// Viewport gizmo pass: the translation widget around the selected entity,
/// the per-component gizmo shapes emitted through the static
/// <see cref="Gizmos"/> API and camera-facing billboard sprites (light icons,
/// selection ring). It is a pure overlay — nothing here writes to the world;
/// interaction moves the selected entity's transform through
/// <see cref="TranslationGizmo"/>. Drawn after the grid, always on top of the
/// scene, and disabled in game.
/// </summary>
public sealed class GizmoRenderer : IDisposable
{
    public const int MaxSprites = 128;

    // Upper bound on gizmo line segments emitted by components each frame.
    private const int MaxGizmoElements = 8192;

    private enum SpriteKind
    {
        Circle = 0,
        Diamond = 1,
        Ring = 2,
        Icon = 3,
        Square = 4
    }

    // Mirrors GizmoSpriteParams in Shaders/Editor/GizmoSprite.slang.
    [StructLayout(LayoutKind.Sequential)]
    private struct GizmoSpriteParams
    {
        public Vector4 Center;
        public Vector4 Color;
        public Vector4 ScaleKind; // x = half-size (world), y = kind (0 circle, 1 diamond, 2 ring, 3 icon, 4 square)
        public Vector4 UvRect;    // icon atlas rectangle (u0, v0, u1, v1)
    }

    // Mirrors GizmoWidgetElement in Shaders/Editor/GizmoLine.slang.
    [StructLayout(LayoutKind.Sequential)]
    private struct GizmoWidgetElement
    {
        public Vector4 Start;    // xyz = world start (widget origin)
        public Vector4 End;      // xyz = world end (shaft tip / head apex)
        public Vector4 Color;
        public Vector4 Sizes;    // shaft: x = kind (0), y = half-width (px);
                                 // cone:  x = kind (1), z = length (world), w = radius (world)
                                 // ring:  x = kind (2), z = ring radius (world), w = tube radius (world)
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
    private const int RingSegments = 48;         // rotation ring tessellation (around the circle)
    private const int RingTubeSegments = 8;       // tube cross-section tessellation
    private const int RingVertexCount = RingSegments * RingTubeSegments * 6;
    private const float RingTubeRadiusPx = 3f;    // 6px thick ring tube, matching the shafts

    private readonly IGraphicsDevice _device;
    private readonly IBuffer _cameraBuffer;
    private readonly ulong _cameraBufferSize;
    private GizmoIconAtlas _iconAtlas = null!;
    private readonly GizmoSpriteParams[] _spriteParams = new GizmoSpriteParams[MaxSprites];
    private readonly GizmoWidgetElement[] _widgetElements = new GizmoWidgetElement[6];
    private readonly GizmoWidgetElement[] _ringElements = new GizmoWidgetElement[3];
    private readonly GizmoWidgetElement[] _gizmoElements = new GizmoWidgetElement[MaxGizmoElements];
    private readonly GizmoLineBatch _lineBatch = new();
    private IPipeline _widgetPipeline = null!;
    private IPipeline _spritePipeline = null!;
    private IBuffer _shaftVertexBuffer = null!;
    private IBuffer _coneVertexBuffer = null!;
    private IBuffer _ringVertexBuffer = null!;
    private IBuffer _spriteVertexBuffer = null!;
    private IBuffer _shaftElementsBuffer = null!;
    private IBuffer _coneElementsBuffer = null!;
    private IBuffer _ringElementsBuffer = null!;
    private IBuffer _gizmoElementsBuffer = null!;
    private IBuffer _spriteParamsBuffer = null!;
    private IBindGroup _shaftBindGroup = null!;
    private IBindGroup _coneBindGroup = null!;
    private IBindGroup _ringBindGroup = null!;
    private IBindGroup _gizmoBindGroup = null!;
    private IBindGroup _spriteBindGroup = null!;
    private bool _wasMouseDown;
    private bool _disposed;
    private GizmoMode _mode = GizmoMode.Translate;

    public GizmoRenderer(IGraphicsDevice device, IBuffer cameraBuffer, ulong cameraBufferSize)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _cameraBuffer = cameraBuffer ?? throw new ArgumentNullException(nameof(cameraBuffer));
        _cameraBufferSize = cameraBufferSize;
        CreateResources();
    }

    /// <summary>Translate widget logic (hover/drag/snap). Configure snapping through <see cref="SnapSize"/>.</summary>
    public TranslationGizmo Translate { get; } = new();

    /// <summary>Rotate widget logic (hover/drag/degree snapping).</summary>
    public RotationGizmo Rotation { get; } = new();

    /// <summary>Scale widget logic (hover/drag/snapping).</summary>
    public ScaleGizmo Scale { get; } = new();

    /// <summary>The gizmo tool the viewport currently edits.</summary>
    public GizmoMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value)
                return;
            _mode = value;
            // Switching tools mid-drag must never strand an active axis.
            Translate.EndDrag();
            Rotation.EndDrag();
            Scale.EndDrag();
        }
    }

    /// <summary>The gizmo active for the current <see cref="Mode"/>.</summary>
    public Gizmo ActiveGizmo => Mode switch
    {
        GizmoMode.Rotate => Rotation,
        GizmoMode.Scale => Scale,
        _ => Translate
    };

    /// <summary>True while any widget gizmo is dragging the selection.</summary>
    public bool IsDragging => ActiveGizmo.IsDragging;

    /// <summary>The entity the widget follows; null hides the widget.</summary>
    public Entity? Selection { get; set; }

    /// <summary>Whether the pass draws anything (off = pure game viewport).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Whether lights in the world get billboard icons.</summary>
    public bool ShowLightSprites { get; set; } = true;

    /// <summary>Whether non-selected mesh entities get billboard icons.</summary>
    public bool ShowEntitySprites { get; set; } = true;

    /// <summary>Snap size for dragged translation in world units, or null to move freely.</summary>
    public float? SnapSize
    {
        get => Translate.SnapSize;
        set => Translate.SnapSize = value;
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

        var gizmo = ActiveGizmo;
        gizmo.SetTarget(Selection?.GetComponent<TransformComponent>());
        if (gizmo.Target is null)
            return;

        // The widget is drawn at a constant screen size; the hover tests the
        // same world-space extent, so both stay in sync at any camera distance.
        gizmo.ScreenSizePx = WidgetPixelSize;

        var ray = matrices.RayFromScreen(mousePixels);

        var pressed = mouseDown && !_wasMouseDown;
        var released = !mouseDown && _wasMouseDown;
        _wasMouseDown = mouseDown;

        if (released)
            gizmo.EndDrag();

        if (pressed)
        {
            gizmo.UpdateHover(ray, mousePixels, matrices.View, matrices.Projection, matrices.Width, matrices.Height);
            if (gizmo.HoveredAxis != GizmoAxis.None)
                gizmo.BeginDrag(ray);
        }
        else if (mouseDown && gizmo.IsDragging)
        {
            gizmo.Drag(ray);
        }
        else
        {
            gizmo.UpdateHover(ray, mousePixels, matrices.View, matrices.Projection, matrices.Width, matrices.Height);
        }
    }

    /// <summary>Draws the widget, the per-component gizmo shapes and the billboard sprites on top of the scene.</summary>
    public void Draw(IRenderPass pass, World? world, Camera camera, int width, int height)
    {
        if (_disposed || !Enabled)
            return;

        var target = Selection?.GetComponent<TransformComponent>();
        ActiveGizmo.SetTarget(target);

        if (target is null && world is null)
            return;

        var spriteCount = 0;

        if (target is not null)
            DrawWidget(pass, ref spriteCount, camera, width, height);

        if (world is not null)
            DrawComponentGizmos(pass, world, width, height);

        if (world is not null && ShowLightSprites)
        {
            foreach (var light in world.Query<Light>())
            {
                if (!light.Enabled)
                    continue;

                var lightIcon = ResolveIcon(light);
                if (lightIcon is null)
                    continue;

                var position = light is SpotLight spot
                    ? spot.World.Position
                    : light.World.Position;
                AddIconSprite(ref spriteCount, position, new Vector4(light.Color, 0.95f),
                    ScreenHalfSize(position, SpritePixelSize * 0.5f, camera, height), lightIcon.Value);
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
                    ScreenHalfSize(position, SpritePixelSize * 0.5f, camera, height), meshIcon.Value);
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
    /// Runs every enabled component's <see cref="Component.OnDrawGizmo"/> hook
    /// inside a <see cref="Gizmos"/> scope, then uploads and draws the emitted
    /// line segments as screen-space-thick shafts through the widget pipeline.
    /// </summary>
    private void DrawComponentGizmos(IRenderPass pass, World world, int width, int height)
    {
        _lineBatch.Clear();
        using (Gizmos.Begin(_lineBatch, Selection))
        {
            foreach (var entity in world.Entities.ToArray())
            {
                if (!entity.IsValid)
                    continue;
                foreach (var component in entity.Components.ToArray())
                {
                    if (!component.IsValid || !component.Enabled)
                        continue;
                    component.RunDrawGizmo();
                }
            }
        }

        if (_lineBatch.Count == 0)
            return;

        var count = Math.Min(_lineBatch.Count, MaxGizmoElements);
        for (var i = 0; i < count; i++)
        {
            var line = _lineBatch.Lines[i];
            _gizmoElements[i] = new GizmoWidgetElement
            {
                Start = new Vector4(line.Start, 1f),
                End = new Vector4(line.End, 1f),
                Color = line.Color,
                Sizes = new Vector4(0f, Math.Max(0.5f, line.Thickness * 0.5f), 0f, 0f),
                Viewport = new Vector4(width, height, 0f, 0f)
            };
        }

        WriteGizmoElements(count);
        pass.SetPipeline(_widgetPipeline);
        pass.SetBindGroup(_gizmoBindGroup, 0);
        pass.SetVertexBuffer(_shaftVertexBuffer, _shaftVertexBuffer.Size);
        pass.DrawInstanced(6, (uint)count);
    }

    /// <summary>Draws the active widget (translate/rotate/scale) around the selection.</summary>
    private void DrawWidget(IRenderPass pass, ref int spriteCount, Camera camera, int width, int height)
    {
        var gizmo = ActiveGizmo;
        var origin = gizmo.Origin;
        var aspect = Math.Max(1, width) / (float)Math.Max(1, height);
        var widgetScale = Gizmo.ScreenConstantWorldSize(origin, camera.ViewMatrix,
            camera.ProjectionMatrix(aspect), height, gizmo.ScreenSizePx);
        var hovered = (float)gizmo.HoveredAxis;
        var active = (float)gizmo.ActiveAxis;

        // Selection ring around the widget origin.
        AddSprite(ref spriteCount, origin, SelectionColor, ScreenHalfSize(origin, 28f, camera, height), SpriteKind.Ring);

        switch (Mode)
        {
            case GizmoMode.Rotate:
                DrawRotationWidget(pass, gizmo, origin, widgetScale, hovered, active, camera, width, height);
                break;
            case GizmoMode.Scale:
                DrawScaleWidget(pass, ref spriteCount, gizmo, origin, widgetScale, hovered, active, camera, width, height);
                break;
            default:
                DrawTranslationWidget(pass, gizmo, origin, widgetScale, hovered, active, camera, width, height);
                break;
        }
    }

    /// <summary>Draws the translate widget: three screen-space shafts with 3D cone heads.</summary>
    private void DrawTranslationWidget(IRenderPass pass, Gizmo gizmo, Vector3 origin, float widgetScale, float hovered, float active, Camera camera, int width, int height)
    {
        // Thick shafts (screen-space quads) and true 3D cone heads, colored
        // by axis state. The first three elements are shafts; the last three
        // are cones and are uploaded to separate storage buffers below.
        for (var axis = GizmoAxis.X; axis <= GizmoAxis.Z; axis++)
        {
            var direction = gizmo.AxisDirection(axis);
            var tip = origin + direction * widgetScale;
            var tipDepth = Math.Max(1e-4f, Vector3.Dot(camera.Forward, tip - camera.Position));
            var coneLength = ScreenWorldSize(HeadLengthPx, tipDepth, camera, height);
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
        for (var axis = GizmoAxis.X; axis <= GizmoAxis.Z; axis++)
        {
            var tip = origin + gizmo.AxisDirection(axis) * widgetScale;
            var tipDepth = Math.Max(1e-4f, Vector3.Dot(camera.Forward, tip - camera.Position));
            var coneLength = ScreenWorldSize(HeadLengthPx, tipDepth, camera, height);
            var coneRadius = ScreenWorldSize(HeadHalfWidthPx, tipDepth, camera, height);
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

    /// <summary>Draws the scale widget: three shafts with square billboard handles at their tips.</summary>
    private void DrawScaleWidget(IRenderPass pass, ref int spriteCount, Gizmo gizmo, Vector3 origin, float widgetScale, float hovered, float active, Camera camera, int width, int height)
    {
        for (var axis = GizmoAxis.X; axis <= GizmoAxis.Z; axis++)
        {
            var tip = origin + gizmo.AxisDirection(axis) * widgetScale;
            _widgetElements[(int)axis] = new GizmoWidgetElement
            {
                Start = new Vector4(origin, 1f),
                End = new Vector4(tip, 1f),
                Color = AxisStateColor(axis, hovered, active),
                Sizes = new Vector4(0f, ShaftHalfWidthPx, 0f, 0f),
                Viewport = new Vector4(width, height, 0f, 0f)
            };
        }
        WriteWidgetElements(_shaftElementsBuffer, 0, 3);

        pass.SetPipeline(_widgetPipeline);
        pass.SetBindGroup(_shaftBindGroup, 0);
        pass.SetVertexBuffer(_shaftVertexBuffer, _shaftVertexBuffer.Size);
        pass.DrawInstanced(6, 3);

        // Square handles (screen-facing, like the icon sprites) at each tip.
        for (var axis = GizmoAxis.X; axis <= GizmoAxis.Z; axis++)
        {
            var tip = origin + gizmo.AxisDirection(axis) * widgetScale;
            AddSprite(ref spriteCount, tip, AxisStateColor(axis, hovered, active),
                ScreenHalfSize(tip, HeadHalfWidthPx, camera, height), SpriteKind.Square);
        }
    }

    /// <summary>Draws the rotate widget: one 3D ring per axis, in the plane perpendicular to it.</summary>
    private void DrawRotationWidget(IRenderPass pass, Gizmo gizmo, Vector3 origin, float widgetScale, float hovered, float active, Camera camera, int width, int height)
    {
        for (var axis = GizmoAxis.X; axis <= GizmoAxis.Z; axis++)
        {
            var direction = gizmo.AxisDirection(axis);
            var ringDepth = Math.Max(1e-4f, Vector3.Dot(camera.Forward, origin - camera.Position));
            var tubeRadius = ScreenWorldSize(RingTubeRadiusPx, ringDepth, camera, height);
            _ringElements[(int)axis] = new GizmoWidgetElement
            {
                Start = new Vector4(origin, 1f),
                End = new Vector4(origin + direction, 1f),
                Color = AxisStateColor(axis, hovered, active),
                Sizes = new Vector4(2f, 0f, widgetScale, tubeRadius),
                Viewport = new Vector4(width, height, 0f, 0f)
            };
        }

        WriteRingElements();
        pass.SetPipeline(_widgetPipeline);
        pass.SetBindGroup(_ringBindGroup, 0);
        pass.SetVertexBuffer(_ringVertexBuffer, _ringVertexBuffer.Size);
        pass.DrawInstanced((uint)RingVertexCount, 3);
    }

    /// <summary>World half-size of an object spanning <paramref name="pixels"/> on screen at <paramref name="position"/>.</summary>
    private static float ScreenHalfSize(Vector3 position, float pixels, Camera camera, int height)
    {
        var depth = Vector3.Dot(camera.Forward, position - camera.Position);
        // FieldOfView is degrees; the screen-size math needs the half-angle in radians.
        var tanHalfFov = MathF.Tan(camera.FieldOfView * MathF.PI / 180f * 0.5f);
        return pixels * 2f * Math.Max(1e-4f, depth) * tanHalfFov / Math.Max(1, height);
    }

    /// <summary>World size of an object spanning <paramref name="pixels"/> on screen at the given forward <paramref name="depth"/>.</summary>
    private static float ScreenWorldSize(float pixels, float depth, Camera camera, int height)
    {
        var tanHalfFov = MathF.Tan(camera.FieldOfView * MathF.PI / 180f * 0.5f);
        return pixels * 2f * Math.Max(1e-4f, depth) * tanHalfFov / Math.Max(1, height);
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

    /// <summary>Uploads the recorded component gizmo lines into their storage buffer.</summary>
    private void WriteGizmoElements(int count)
    {
        var bytes = new byte[count * sizeof(GizmoWidgetElement)];
        unsafe
        {
            fixed (byte* destination = bytes)
            {
                var elements = (GizmoWidgetElement*)destination;
                for (var i = 0; i < count; i++)
                    elements[i] = _gizmoElements[i];
            }
        }
        _gizmoElementsBuffer.Write(bytes);
    }

    /// <summary>Uploads the three rotation ring elements into their storage buffer.</summary>
    private void WriteRingElements()
    {
        var bytes = new byte[3 * sizeof(GizmoWidgetElement)];
        unsafe
        {
            fixed (byte* destination = bytes)
            {
                var elements = (GizmoWidgetElement*)destination;
                for (var i = 0; i < 3; i++)
                    elements[i] = _ringElements[i];
            }
        }
        _ringElementsBuffer.Write(bytes);
    }

    /// <summary>
    /// Builds the rotation ring's vertices: a torus swept around the unit
    /// circle, with a cross-section tube so the ring keeps a constant screen
    /// thickness instead of collapsing to a hairline when viewed edge-on.
    /// </summary>
    internal static float[] BuildRingVertices()
    {
        var vertices = new float[RingVertexCount * 4];
        for (var segment = 0; segment < RingSegments; segment++)
        {
            var ringAngle0 = segment * MathF.Tau / RingSegments;
            var ringAngle1 = (segment + 1) * MathF.Tau / RingSegments;
            for (var tube = 0; tube < RingTubeSegments; tube++)
            {
                var tubeAngle0 = tube * MathF.Tau / RingTubeSegments;
                var tubeAngle1 = (tube + 1) * MathF.Tau / RingTubeSegments;
                var offset = (segment * RingTubeSegments + tube) * 24;
                // Tube cross-section quad between (θ,φ) corners: two triangles.
                WriteRingVertex(vertices, offset, ringAngle0, tubeAngle0);
                WriteRingVertex(vertices, offset + 4, ringAngle1, tubeAngle0);
                WriteRingVertex(vertices, offset + 8, ringAngle0, tubeAngle1);
                WriteRingVertex(vertices, offset + 12, ringAngle1, tubeAngle0);
                WriteRingVertex(vertices, offset + 16, ringAngle0, tubeAngle1);
                WriteRingVertex(vertices, offset + 20, ringAngle1, tubeAngle1);
            }
        }
        return vertices;
    }

    /// <summary>Writes one ring vertex: (cosθ, sinθ, cosφ, sinφ) around the ring and tube.</summary>
    private static void WriteRingVertex(float[] vertices, int offset, float ringAngle, float tubeAngle)
    {
        vertices[offset] = MathF.Cos(ringAngle);
        vertices[offset + 1] = MathF.Sin(ringAngle);
        vertices[offset + 2] = MathF.Cos(tubeAngle);
        vertices[offset + 3] = MathF.Sin(tubeAngle);
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

    private static Vector4 AxisStateColor(GizmoAxis axis, float hovered, float active)
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

        // Rotation ring: a torus swept around the unit circle. Each vertex is
        // (cosθ, sinθ, cosφ, sinφ) where θ walks the ring and φ walks the tube
        // cross-section; the shader expands it into a world-space torus whose
        // tube keeps a constant screen thickness even when viewed edge-on.
        var ringVertices = BuildRingVertices();
        _ringVertexBuffer = CreateBuffer((ulong)(ringVertices.Length * sizeof(float)), BufferUsage.Vertex | BufferUsage.CopyDst);
        unsafe
        {
            fixed (float* data = ringVertices)
                _ringVertexBuffer.Write(new ReadOnlySpan<byte>(data, ringVertices.Length * sizeof(float)));
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
        _ringElementsBuffer = CreateBuffer((ulong)(3 * sizeof(GizmoWidgetElement)), BufferUsage.Storage | BufferUsage.CopyDst);
        _gizmoElementsBuffer = CreateBuffer((ulong)(MaxGizmoElements * sizeof(GizmoWidgetElement)), BufferUsage.Storage | BufferUsage.CopyDst);
        _spriteParamsBuffer = CreateBuffer((ulong)(MaxSprites * sizeof(GizmoSpriteParams)), BufferUsage.Storage | BufferUsage.CopyDst);
        _iconAtlas = GizmoIconAtlas.Load(_device);

        // Both gizmo shaders include Common/Camera.slang, which slangc
        // flattens into the WGSL that Shader.Load returns.
        var widgetShader = Shader.Load(PathUtil.Combine("Shaders", "Editor/GizmoLine.wgsl"));
        var spriteShader = Shader.Load(PathUtil.Combine("Shaders", "Editor/GizmoSprite.wgsl"));

        _widgetPipeline = _device.CreatePipeline(new PipelineDescription
        {
            ShaderSource = widgetShader.Source,
            VertexEntryPoint = "vs_main",
            FragmentEntryPoint = "fs_main",
            // The gizmos draw into the linear HDR scene target (tonemapped by
            // the post-process pass like the rest of the frame).
            ColorFormat = TextureFormat.Rgba16Float,
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
            BindGroups = widgetShader.BuildBindGroupLayouts()
        });
        _shaftBindGroup = _widgetPipeline.CreateBindGroup(
        [
            new BindGroupBinding { Slot = 0, Buffer = _cameraBuffer, BufferSize = _cameraBufferSize },
            new BindGroupBinding { Slot = 1, Buffer = _shaftElementsBuffer, BufferSize = (ulong)(3 * sizeof(GizmoWidgetElement)) }
        ]);
        _coneBindGroup = _widgetPipeline.CreateBindGroup(
        [
            new BindGroupBinding { Slot = 0, Buffer = _cameraBuffer, BufferSize = _cameraBufferSize },
            new BindGroupBinding { Slot = 1, Buffer = _coneElementsBuffer, BufferSize = (ulong)(3 * sizeof(GizmoWidgetElement)) }
        ]);
        _ringBindGroup = _widgetPipeline.CreateBindGroup(
        [
            new BindGroupBinding { Slot = 0, Buffer = _cameraBuffer, BufferSize = _cameraBufferSize },
            new BindGroupBinding { Slot = 1, Buffer = _ringElementsBuffer, BufferSize = (ulong)(3 * sizeof(GizmoWidgetElement)) }
        ]);
        _gizmoBindGroup = _widgetPipeline.CreateBindGroup(
        [
            new BindGroupBinding { Slot = 0, Buffer = _cameraBuffer, BufferSize = _cameraBufferSize },
            new BindGroupBinding { Slot = 1, Buffer = _gizmoElementsBuffer, BufferSize = (ulong)(MaxGizmoElements * sizeof(GizmoWidgetElement)) }
        ]);

        _spritePipeline = _device.CreatePipeline(new PipelineDescription
        {
            ShaderSource = spriteShader.Source,
            VertexEntryPoint = "vs_main",
            FragmentEntryPoint = "fs_main",
            // Matches the linear HDR scene target (tonemapped in post).
            ColorFormat = TextureFormat.Rgba16Float,
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
            BindGroups = spriteShader.BuildBindGroupLayouts()
        });
        _spriteBindGroup = _spritePipeline.CreateBindGroup(
        [
            new BindGroupBinding { Slot = 0, Buffer = _cameraBuffer, BufferSize = _cameraBufferSize },
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
        _ringBindGroup?.Dispose();
        _gizmoBindGroup?.Dispose();
        _spriteBindGroup?.Dispose();
        _shaftElementsBuffer?.Dispose();
        _coneElementsBuffer?.Dispose();
        _ringElementsBuffer?.Dispose();
        _gizmoElementsBuffer?.Dispose();
        _spriteParamsBuffer?.Dispose();
        _shaftVertexBuffer?.Dispose();
        _coneVertexBuffer?.Dispose();
        _ringVertexBuffer?.Dispose();
        _spriteVertexBuffer?.Dispose();
        _iconAtlas?.Dispose();
        _widgetPipeline?.Dispose();
        _spritePipeline?.Dispose();
    }
}
