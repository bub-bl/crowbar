using System.Numerics;
using System.Runtime.InteropServices;
using Crowbar.Engine;
using Crowbar.UI;

namespace Crowbar.Engine.Rendering;

/// <summary>
/// Runtime renderer. Owns the 3D scene pass (the world's
/// <see cref="MeshRenderer"/> components), the offscreen scene texture and
/// the Skia-UI compositing (texture upload + fills/backdrops/decorations
/// quads), and records every frame through the backend-neutral
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
    [StructLayout(LayoutKind.Sequential)]
    private struct CameraUniforms
    {
        public Matrix4x4 View;
        public Matrix4x4 Projection;
    }

    // Mirrors MeshUniforms in Shaders/Mesh.wgsl (model/view/proj, color,
    // lightDir, isSelected). 224 bytes, no padding between fields.
    [StructLayout(LayoutKind.Sequential)]
    private struct MeshUniforms
    {
        public Matrix4x4 Model;
        public Matrix4x4 View;
        public Matrix4x4 Projection;
        public Vector4 Color;
        public Vector3 LightDir;
        public uint IsSelected;
    }

    /// <summary>GPU geometry of one <see cref="Mesh"/>, shared by every renderable using it.</summary>
    private sealed class MeshBuffers
    {
        public required IBuffer VertexBuffer { get; init; }
        public required IBuffer IndexBuffer { get; init; }
    }

    /// <summary>Per-renderable GPU state: one uniform (model + camera + material) and its bind group.</summary>
    private sealed class RenderableResources
    {
        public required IBuffer UniformBuffer { get; init; }
        public required IBindGroup BindGroup { get; init; }
    }

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

    private readonly IGraphicsDevice _device;
    private int _width;
    private int _height;
    private bool _disposed;

    // Camera: the per-frame view/projection, copied into every renderable's uniform.
    private CameraUniforms _camera;

    // Mesh scene pass: one pipeline (Mesh.wgsl), GPU buffers cached per Mesh,
    // uniforms + bind groups cached per component. Caches are rebuilt and
    // pruned each frame from the world's MeshRenderers.
    private IPipeline _meshPipeline = null!;
    private readonly Dictionary<Mesh, MeshBuffers> _meshBuffers = [];
    private readonly Dictionary<MeshRenderer, RenderableResources> _renderables = [];
    private Material? _defaultMaterial;

    // Offscreen 3D scene: the cube renders here instead of directly on the
    // surface, then the scene is blitted to the surface. backdrop-filter
    // panels are composited on the GPU by Backdrop.wgsl sampling this texture
    // directly (like S&box's ui_backdropfilter.shader), so the CPU never sees
    // the scene and the Skia UI raster only re-runs when the UI changes.
    private ITexture _sceneTexture = null!;
    private IBindGroup _sceneBindGroup = null!;

    private ITexture _depthTexture = null!;

    // UI compositing: one instanced fullscreen quad per region.
    private ITexture _uiTexture = null!;
    private ISampler _uiSampler = null!;
    private IPipeline _uiPipeline = null!;
    private IBuffer _uiVertexBuffer = null!;
    private IBindGroup _uiBindGroup = null!;
    private int _uiWidth;
    private int _uiHeight;
    private bool _uiTextureDirty = true;
    private List<UiRectInt> _fullScreenDamage = [];

    // Backdrop compositor (16 regions max).
    private const uint MaxBackdropRegions = 16;
    private IPipeline _backdropPipeline = null!;
    private IBindGroup _backdropBindGroup = null!;
    private IBuffer _backdropParamsBuffer = null!;
    private byte[]? _backdropParamsBytes;

    // GPU decorations: outer box-shadows + uniform solid borders (256 regions max).
    private const uint MaxDecorations = 256;
    private IPipeline _decoPipeline = null!;
    private IBindGroup _decoBindGroup = null!;
    private IBuffer _decoParamsBuffer = null!;
    private IBuffer _decoUniformBuffer = null!;
    private byte[]? _decoParamsBytes;

    // GPU fills: solid panel backgrounds (1024 regions max).
    private const uint MaxFills = 1024;
    private IPipeline _fillPipeline = null!;
    private IBindGroup _fillBindGroup = null!;
    private IBuffer _fillParamsBuffer = null!;
    private IBuffer _fillUniformBuffer = null!;
    private byte[]? _fillParamsBytes;

    public Renderer(IGraphicsDevice device)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _width = device.Width;
        _height = device.Height;

        CreateMeshResources();
        CreateBackdropResources();
        CreateDecorationResources();
        CreateFillResources();
        CreateUiResources(_width, _height);
        CreateDepthTexture(_width, _height);
        CreateSceneResources(_width, _height);
        UpdateCamera(new Camera());
    }

    public void Render(World? world, Camera camera, double _, UiSystem ui)
    {
        if (_disposed)
            return;

        UpdateCamera(camera);

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
                DrawMeshRenderers(scenePass, world);
            }

            // Rasterize and upload the UI before reading any GPU-composited regions.
            // The renderer recalculates fills, backdrops and decorations while it
            // renders. Reading those lists before Ui.Render() made the compositor
            // use the previous frame's geometry, while the texture already contained
            // the current frame. That one-frame skew is especially visible when an
            // animation changes transform, hover shadows or fill delegation: stale
            // quads then expose/cover the wrong pixels and look like ghosted edges.
            bool uiChanged = false;
            if (ui is not null)
            {
                uiChanged = _uiTextureDirty || ui.IsDirty;
                ui.Render();
                if (uiChanged && ui.Renderer.PixelBuffer != 0)
                {
                    // Skia's premultiplied pixels are uploaded as-is (no CPU
                    // conversion): the UI shader un-premultiplies and decodes sRGB.
                    // Only the damaged sub-rects are copied, so paint-only changes
                    // (hover, caret, animation ticks) upload a handful of small
                    // regions instead of the whole 1280x720 texture.
                    var damage = _uiTextureDirty ? _fullScreenDamage : ui.Renderer.DamageRects;
                    if (damage.Count > 0)
                    {
                        UpdateUiTexture(ui.Renderer.PixelBuffer, ui.Renderer.RowBytes, damage);
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
                surfacePass.SetPipeline(_uiPipeline);
                surfacePass.SetBindGroup(_sceneBindGroup, 0);
                surfacePass.SetVertexBuffer(_uiVertexBuffer, 6 * 4 * sizeof(float));
                surfacePass.Draw(6);

                // GPU fills: the solid panel backgrounds the Skia raster skipped (see
                // SkiaUiRenderer.CollectFills) are composited as one instanced quad per
                // region below the UI texture, in paint order. The texture keeps only
                // the non-delegated paint (text, images, fallback fills), so an opaque
                // fill is a plain overwrite of the scene and translucent fills blend
                // over it exactly like the raster would. The renderer only delegates
                // fills whose area is clean of earlier CPU paint, so no texture pixel
                // can hide under a quad.
                var fills = ui?.Renderer.Fills;
                if (fills is { Count: > 0 })
                {
                    UpdateFillParams(fills);
                    if (currentPipeline != _fillPipeline)
                    {
                        surfacePass.SetPipeline(_fillPipeline);
                        currentPipeline = _fillPipeline;
                    }

                    surfacePass.SetBindGroup(_fillBindGroup, 0);
                    surfacePass.SetVertexBuffer(_uiVertexBuffer, 6 * 4 * sizeof(float));
                    surfacePass.DrawInstanced(6, (uint)Math.Min(fills.Count, MaxFills));
                }

                // backdrop-filter: one instanced fullscreen quad per region, sampling
                // the 3D scene texture with blur + color transforms. The params come
                // from the Skia raster pass, which only records regions it could not
                // bake on the CPU itself, so moving the camera never re-rasterizes the
                // UI. The UI overlay is drawn on top afterwards (regions are painted
                // between the scene and the UI, like S&box's ui_backdropfilter).
                var backdrops = ui?.Renderer.Backdrops;
                if (backdrops is { Count: > 0 })
                {
                    UpdateBackdropParams(backdrops);
                    surfacePass.SetPipeline(_backdropPipeline);
                    currentPipeline = _backdropPipeline;
                    surfacePass.SetBindGroup(_backdropBindGroup, 0);
                    surfacePass.SetVertexBuffer(_uiVertexBuffer, 6 * 4 * sizeof(float));
                    surfacePass.DrawInstanced(6, (uint)Math.Min(backdrops.Count, MaxBackdropRegions));
                }

                if (ui is not null)
                {
                    if (currentPipeline != _uiPipeline)
                    {
                        surfacePass.SetPipeline(_uiPipeline);
                        currentPipeline = _uiPipeline;
                    }

                    surfacePass.SetBindGroup(_uiBindGroup, 0);
                    surfacePass.SetVertexBuffer(_uiVertexBuffer, 6 * 4 * sizeof(float));
                    surfacePass.Draw(6);
                }

                // GPU decorations (outer box-shadows + uniform borders): the Skia
                // raster skips them and emits one instanced quad per region, drawn
                // above the UI texture. The renderer only delegates decorations that
                // nothing painted later can cover and that no clip cuts (see
                // SkiaUiRenderer.CollectDecorations), so this reproduces the CSS paint
                // order on top of the flat UI layer.
                var decorations = ui?.Renderer.Decorations;
                if (decorations is { Count: > 0 })
                {
                    UpdateDecorationParams(decorations);
                    if (currentPipeline != _decoPipeline)
                    {
                        surfacePass.SetPipeline(_decoPipeline);
                        currentPipeline = _decoPipeline;
                    }

                    surfacePass.SetBindGroup(_decoBindGroup, 0);
                    surfacePass.SetVertexBuffer(_uiVertexBuffer, 6 * 4 * sizeof(float));
                    surfacePass.DrawInstanced(6, (uint)Math.Min(decorations.Count, MaxDecorations));
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
        CreateUiResources(_width, _height);
        CreateDepthTexture(_width, _height);
        CreateSceneResources(_width, _height);
    }

    private void UpdateCamera(Camera camera)
    {
        float aspect = Math.Max(1, _width) / (float)Math.Max(1, _height);
        _camera = new CameraUniforms
        {
            View = camera.ViewMatrix,
            Projection = camera.ProjectionMatrix(aspect)
        };
    }

    private void CreateMeshResources()
    {
        string shaderSource = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Shaders", "Mesh.wgsl"));

        _meshPipeline = _device.CreatePipeline(new PipelineDescription
        {
            ShaderSource = shaderSource,
            VertexEntryPoint = "vs_main",
            FragmentEntryPoint = "fs_main",
            ColorFormat = _device.Swapchain.Format,
            DepthFormat = TextureFormat.Depth24Plus,
            DepthWriteEnabled = true,
            DepthCompare = CompareFunction.Less,
            VertexLayout = new VertexBufferLayoutDescription
            {
                Stride = 6 * sizeof(float),
                Attributes =
                [
                    new VertexAttributeDescription { Format = VertexFormat.Float32x3, Offset = 0, ShaderLocation = 0 },
                    new VertexAttributeDescription { Format = VertexFormat.Float32x3, Offset = 3 * sizeof(float), ShaderLocation = 1 }
                ]
            },
            Bindings =
            [
                new BindGroupLayoutBinding { Slot = 0, Type = BindingType.UniformBuffer, Stages = ShaderStage.Vertex }
            ]
        });
        _defaultMaterial = Material.CreateDefault(Shader.Load(Path.Combine("Shaders", "Mesh.wgsl")));
    }

    /// <summary>Draws every living <see cref="MeshRenderer"/> in the world at its world transform.</summary>
    private void DrawMeshRenderers(IRenderPass pass, World? world)
    {
        if (world is null)
            return;

        // Materialize once: components may be destroyed while we draw.
        var renderers = world.Query<MeshRenderer>().ToList();
        if (renderers.Count == 0)
            return;

        pass.SetPipeline(_meshPipeline);

        // Release GPU state for renderables whose component was destroyed.
        foreach (var stale in _renderables.Keys.Except(renderers).ToArray())
        {
            if (_renderables.Remove(stale, out var resources))
            {
                resources.BindGroup.Dispose();
                resources.UniformBuffer.Dispose();
            }
        }

        var defaultMaterial = _defaultMaterial;
        var lightDir = Vector3.Normalize(new Vector3(0.5f, 1f, 0.7f));

        foreach (var renderer in renderers)
        {
            if (!renderer.IsValid || renderer.Model is null)
                continue;

            var renderable = GetRenderableResources(renderer);
            var material = renderer.Material ?? defaultMaterial;
            var modelMatrix = ToWorldMatrix(renderer.World);

            foreach (var mesh in renderer.Model.Meshes)
            {
                var buffers = GetMeshBuffers(mesh);
                var uniforms = new MeshUniforms
                {
                    Model = modelMatrix,
                    View = _camera.View,
                    Projection = _camera.Projection,
                    Color = material.Get<Vector4>("color", new Vector4(1f)),
                    LightDir = material.Get<Vector3>("lightDir", lightDir),
                    IsSelected = 0
                };
                renderable.UniformBuffer.Write(in uniforms);

                pass.SetBindGroup(renderable.BindGroup, 0);
                pass.SetVertexBuffer(buffers.VertexBuffer, buffers.VertexBuffer.Size);
                pass.SetIndexBuffer(buffers.IndexBuffer, buffers.IndexBuffer.Size);
                pass.DrawIndexed((uint)mesh.Indices.Length);
            }
        }
    }

    /// <summary>Uploads one mesh's geometry once; shared by every renderable using the same mesh.</summary>
    private MeshBuffers GetMeshBuffers(Mesh mesh)
    {
        if (_meshBuffers.TryGetValue(mesh, out var existing))
            return existing;

        // Mesh.wgsl consumes interleaved position + normal floats (stride 24).
        var floats = new float[mesh.Vertices.Length * 6];
        for (var i = 0; i < mesh.Vertices.Length; i++)
        {
            var vertex = mesh.Vertices[i];
            var offset = i * 6;
            floats[offset] = vertex.Position.X;
            floats[offset + 1] = vertex.Position.Y;
            floats[offset + 2] = vertex.Position.Z;
            floats[offset + 3] = vertex.Normal.X;
            floats[offset + 4] = vertex.Normal.Y;
            floats[offset + 5] = vertex.Normal.Z;
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

    /// <summary>Creates (or returns) the per-component uniform buffer and bind group.</summary>
    private RenderableResources GetRenderableResources(MeshRenderer renderer)
    {
        if (_renderables.TryGetValue(renderer, out var existing))
            return existing;

        var uniformBuffer = _device.CreateBuffer(new BufferDescription
        {
            Size = (ulong)sizeof(MeshUniforms),
            Usage = BufferUsage.Uniform | BufferUsage.CopyDst
        });
        var bindGroup = _meshPipeline.CreateBindGroup(
        [
            new BindGroupBinding { Slot = 0, Buffer = uniformBuffer, BufferSize = (ulong)sizeof(MeshUniforms) }
        ]);

        var resources = new RenderableResources { UniformBuffer = uniformBuffer, BindGroup = bindGroup };
        _renderables.Add(renderer, resources);
        return resources;
    }

    private static Matrix4x4 ToWorldMatrix(Transform transform) =>
        Matrix4x4.CreateScale(transform.Scale)
        * Matrix4x4.CreateFromQuaternion(transform.Rotation.Quaternion)
        * Matrix4x4.CreateTranslation(transform.Position);

    private void CreateUiResources(int width, int height)
    {
        _uiBindGroup?.Dispose();
        _uiTexture?.Dispose();

        _uiTexture = _device.CreateTexture(new TextureDescription
        {
            Width = Math.Max(1, width),
            Height = Math.Max(1, height),
            // Plain unorm format: Skia's premultiplied sRGB-encoded bytes are
            // uploaded raw, and Ui.wgsl un-premultiplies + decodes sRGB in the
            // fragment shader. The old sRGB format made the hardware decode the
            // premultiplied values, flattening translucent colors to near-black.
            Format = TextureFormat.Rgba8Unorm,
            Sampled = true,
            CopyDestination = true
        });
        _uiWidth = Math.Max(1, width);
        _uiHeight = Math.Max(1, height);
        _uiTextureDirty = true;
        _fullScreenDamage = [new UiRectInt(0, 0, _uiWidth, _uiHeight)];

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
            Bindings =
            [
                new BindGroupLayoutBinding { Slot = 0, Type = BindingType.Texture, Stages = ShaderStage.Fragment },
                new BindGroupLayoutBinding { Slot = 1, Type = BindingType.Sampler, Stages = ShaderStage.Fragment }
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

        _uiBindGroup = _uiPipeline.CreateBindGroup(
        [
            new BindGroupBinding { Slot = 0, Texture = _uiTexture },
            new BindGroupBinding { Slot = 1, Sampler = _uiSampler }
        ]);
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
            Bindings =
            [
                new BindGroupLayoutBinding { Slot = 0, Type = BindingType.Texture, Stages = ShaderStage.Fragment },
                new BindGroupLayoutBinding { Slot = 1, Type = BindingType.Sampler, Stages = ShaderStage.Fragment },
                new BindGroupLayoutBinding { Slot = 2, Type = BindingType.ReadOnlyStorageBuffer, Stages = ShaderStage.Fragment }
            ]
        });
    }

    private void CreateDecorationResources()
    {
        string shaderSource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "Decorations.wgsl"));
        _decoParamsBuffer = _device.CreateBuffer(new BufferDescription
        {
            Size = (ulong)(MaxDecorations * sizeof(DecorationGpuParams)),
            Usage = BufferUsage.Storage | BufferUsage.CopyDst
        });
        _decoUniformBuffer = _device.CreateBuffer(new BufferDescription
        {
            Size = 16,
            Usage = BufferUsage.Uniform | BufferUsage.CopyDst
        });
        _decoParamsBytes = null;

        _decoPipeline = _device.CreatePipeline(new PipelineDescription
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
            Bindings =
            [
                new BindGroupLayoutBinding { Slot = 0, Type = BindingType.ReadOnlyStorageBuffer, Stages = ShaderStage.Vertex | ShaderStage.Fragment },
                new BindGroupLayoutBinding { Slot = 1, Type = BindingType.UniformBuffer, Stages = ShaderStage.Vertex }
            ]
        });
        _decoBindGroup = _decoPipeline.CreateBindGroup(
        [
            new BindGroupBinding { Slot = 0, Buffer = _decoParamsBuffer, BufferSize = (ulong)(MaxDecorations * sizeof(DecorationGpuParams)) },
            new BindGroupBinding { Slot = 1, Buffer = _decoUniformBuffer, BufferSize = 16 }
        ]);
    }

    private void CreateFillResources()
    {
        string shaderSource = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Shaders", "Fills.wgsl"));
        _fillParamsBuffer = _device.CreateBuffer(new BufferDescription
        {
            Size = (ulong)(MaxFills * sizeof(FillGpuParams)),
            Usage = BufferUsage.Storage | BufferUsage.CopyDst
        });
        _fillUniformBuffer = _device.CreateBuffer(new BufferDescription
        {
            Size = 16,
            Usage = BufferUsage.Uniform | BufferUsage.CopyDst
        });
        _fillParamsBytes = null;

        _fillPipeline = _device.CreatePipeline(new PipelineDescription
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
            Bindings =
            [
                new BindGroupLayoutBinding { Slot = 0, Type = BindingType.ReadOnlyStorageBuffer, Stages = ShaderStage.Vertex | ShaderStage.Fragment },
                new BindGroupLayoutBinding { Slot = 1, Type = BindingType.UniformBuffer, Stages = ShaderStage.Vertex }
            ]
        });
        _fillBindGroup = _fillPipeline.CreateBindGroup(
        [
            new BindGroupBinding { Slot = 0, Buffer = _fillParamsBuffer, BufferSize = (ulong)(MaxFills * sizeof(FillGpuParams)) },
            new BindGroupBinding { Slot = 1, Buffer = _fillUniformBuffer, BufferSize = 16 }
        ]);
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
    /// Uploads the UI texture from the Skia bitmap's native premultiplied sRGB
    /// pixels. Only the damaged sub-rects are written: each copy uses the
    /// bitmap's row stride as the source layout and the rect origin as the
    /// texture offset, so paint-only changes never touch the full buffer.
    /// The un-premultiply + sRGB decode happens in Ui.wgsl, not on the CPU.
    /// </summary>
    private void UpdateUiTexture(nint pixels, int rowBytes, IReadOnlyList<UiRectInt> damage)
    {
        foreach (var region in damage)
            _uiTexture.Write(pixels, rowBytes, region.X, region.Y, region.Width, region.Height);
    }

    /// <summary>
    /// Translates the fill regions collected by the Skia pass into the GPU
    /// parameter buffer (Fills.wgsl reads one struct per instance) and uploads
    /// it only when the set actually changed. The viewport uniform is rewritten
    /// every time so the vertex stage maps pixel rects correctly.
    /// </summary>
    private void UpdateFillParams(IReadOnlyList<FillRegion> regions)
    {
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
        _fillUniformBuffer.Write(in viewport);
        if (_fillParamsBytes is not null && _fillParamsBytes.AsSpan().SequenceEqual(bytes))
            return;
        _fillParamsBytes = bytes;
        _fillParamsBuffer.Write(bytes);
    }

    /// <summary>
    /// Translates the decoration regions collected by the Skia pass into the
    /// GPU parameter buffer (Decorations.wgsl reads one struct per instance)
    /// and uploads it only when the set actually changed. The viewport uniform
    /// is rewritten every time so the vertex stage maps pixel rects correctly.
    /// </summary>
    private void UpdateDecorationParams(IReadOnlyList<DecorationRegion> regions)
    {
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
        _decoUniformBuffer.Write(in viewport);
        if (_decoParamsBytes is not null && _decoParamsBytes.AsSpan().SequenceEqual(bytes))
            return;
        _decoParamsBytes = bytes;
        _decoParamsBuffer.Write(bytes);
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
        _decoBindGroup?.Dispose();
        _decoParamsBuffer?.Dispose();
        _decoUniformBuffer?.Dispose();
        _decoPipeline?.Dispose();
        _fillBindGroup?.Dispose();
        _fillParamsBuffer?.Dispose();
        _fillUniformBuffer?.Dispose();
        _fillPipeline?.Dispose();
        _uiBindGroup?.Dispose();
        _uiVertexBuffer?.Dispose();
        _uiTexture?.Dispose();
        _uiPipeline?.Dispose();
        _uiSampler?.Dispose();
        foreach (var resources in _renderables.Values)
        {
            resources.BindGroup.Dispose();
            resources.UniformBuffer.Dispose();
        }
        _renderables.Clear();
        foreach (var buffers in _meshBuffers.Values)
        {
            buffers.VertexBuffer.Dispose();
            buffers.IndexBuffer.Dispose();
        }
        _meshBuffers.Clear();
        _meshPipeline?.Dispose();
        _depthTexture?.Dispose();
    }
}
