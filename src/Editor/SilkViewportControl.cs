// using System.Diagnostics;
// using System.Numerics;
// using System.Runtime.InteropServices;
// using Avalonia;
// using Avalonia.Controls;
// using Avalonia.Input;
// using Avalonia.Interactivity;
// using Avalonia.Platform;
// using Avalonia.Threading;
// using Crowbar.Engine;
// using Crowbar.Engine.Rendering;
// using GridApi = Crowbar.Engine.Rendering.Grid;
// using Silk.NET.WebGPU;
// using Buffer = Silk.NET.WebGPU.Buffer;
// using Color = Silk.NET.WebGPU.Color;
//
// namespace Crowbar.Editor;
//
// public unsafe class SilkViewportControl : NativeControlHost
// {
//     private nint _hwnd;
//     private nint _previousWndProc;
//     private NativeWndProc? _nativeWndProc;
//     private Surface* _surface;
//     private WebGpuAdapter? _adapterWrapper;
//     private WebGpuDevice? _deviceWrapper;
//     private WebGpuRuntime? _gpu;
//     private Adapter* _adapter;
//     private Device* _device;
//     private Queue* _queue;
//
//     private WebGpuRuntime WebGpuApi => _gpu ?? throw new InvalidOperationException("WebGPU is not initialized.");
//
//     private TextureFormat _swapChainFormat = TextureFormat.Bgra8Unorm;
//     private Texture* _depthTexture;
//     private TextureView* _depthTextureView;
//
//     private ShaderModule* _meshShaderModule;
//     private ShaderModule* _pbrShaderModule;
//     private ShaderModule* _gridShaderModule;
//     private Shader? _meshShader;
//
//     private RenderPipeline* _meshPipeline;
//     private RenderPipeline* _pbrPipeline;
//     private RenderPipeline* _transparentPbrPipeline;
//     private RenderPipeline* _transparentMeshPipeline;
//     private RenderPipeline* _wireframePipeline;
//     private RenderPipeline* _selectionDepthPipeline;
//     private RenderPipeline* _outlinePipeline;
//     private RenderPipeline* _gridPipeline;
//     private MeshRenderPass? _opaqueMeshPass;
//     private MeshRenderPass? _transparentMeshPass;
//     private MeshRenderPass? _wireframeMeshPass;
//
//     private Buffer* _cubeVbo;
//     private Buffer* _cubeEbo;
//     private Buffer* _cubeWireframeEbo;
//     private Buffer* _pyramidVbo;
//     private Buffer* _pyramidEbo;
//     private Buffer* _pyramidWireframeEbo;
//     private Buffer* _gridVbo;
//
//     private Buffer* _gridUniformBuffer;
//
//     private BindGroup* _gridBindGroup;
//
//     private BindGroupLayout* _meshBindGroupLayout;
//     private BindGroupLayout* _pbrMaterialBindGroupLayout;
//     private BindGroupLayout* _gridBindGroupLayout;
//
//     // QueueWriteBuffer is ordered on the queue. Reusing one uniform buffer for
//     // several draws therefore makes every draw observe the last object's data.
//     // Keep one buffer/bind group per object so each draw has stable uniforms.
//     private readonly Dictionary<SceneObject, MeshGpuResources> _meshResources = new();
//     public GridApi Grid { get; } = new();
//
//     private int _width = 800;
//     private int _height = 600;
//
//     private Point _lastMousePos;
//     private bool _isOrbiting;
//     private bool _isPanning;
//     private bool _moveForward;
//     private bool _moveBackward;
//     private bool _moveLeft;
//     private bool _moveRight;
//
//     private const float CameraMoveSpeed = 5.0f;
//
//     private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
//     private double _lastFrameTime;
//     private int _frameCount;
//     private double _fpsTimer;
//     private Thread? _renderThread;
//     private CancellationTokenSource? _renderCancellation;
//     private Scene? _renderScene;
//     private readonly object _gpuLock = new();
//
//     public static readonly StyledProperty<Scene?> SceneProperty =
//         AvaloniaProperty.Register<SilkViewportControl, Scene?>(nameof(Scene));
//
//     public static readonly StyledProperty<int> FpsProperty =
//         AvaloniaProperty.Register<SilkViewportControl, int>(nameof(Fps));
//
//     public static readonly StyledProperty<float> FrameTimeMsProperty =
//         AvaloniaProperty.Register<SilkViewportControl, float>(nameof(FrameTimeMs));
//
//     public static readonly StyledProperty<string> GpuVendorProperty =
//         AvaloniaProperty.Register<SilkViewportControl, string>(nameof(GpuVendor), "Initializing...");
//
//     public static readonly StyledProperty<string> GpuRendererProperty =
//         AvaloniaProperty.Register<SilkViewportControl, string>(nameof(GpuRenderer), "Initializing...");
//
//     public SilkViewportControl()
//     {
//         Focusable = true;
//
//         PointerPressedEvent.AddClassHandler<SilkViewportControl>((ctrl, e) => ctrl.HandlePointerPressed(e), RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
//         PointerMovedEvent.AddClassHandler<SilkViewportControl>((ctrl, e) => ctrl.HandlePointerMoved(e), RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
//         PointerReleasedEvent.AddClassHandler<SilkViewportControl>((ctrl, e) => ctrl.HandlePointerReleased(e), RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
//         PointerWheelChangedEvent.AddClassHandler<SilkViewportControl>((ctrl, e) => ctrl.HandlePointerWheelChanged(e), RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
//     }
//
//     public Scene? Scene
//     {
//         get => GetValue(SceneProperty);
//         set => SetValue(SceneProperty, value);
//     }
//
//     public int Fps
//     {
//         get => GetValue(FpsProperty);
//         private set => SetValue(FpsProperty, value);
//     }
//
//     public float FrameTimeMs
//     {
//         get => GetValue(FrameTimeMsProperty);
//         private set => SetValue(FrameTimeMsProperty, value);
//     }
//
//     public string GpuVendor
//     {
//         get => GetValue(GpuVendorProperty);
//         private set => SetValue(GpuVendorProperty, value);
//     }
//
//     public string GpuRenderer
//     {
//         get => GetValue(GpuRendererProperty);
//         private set => SetValue(GpuRendererProperty, value);
//     }
//
//     protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
//     {
//         base.OnPropertyChanged(change);
//
//         if (change.Property == SceneProperty)
//         {
//             lock (_gpuLock)
//             {
//                 _renderScene = change.NewValue as Scene;
//             }
//         }
//     }
//
//     protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
//     {
//         if (OperatingSystem.IsWindows())
//         {
//             const int WS_CHILD = 0x40000000;
//             const int WS_VISIBLE = 0x10000000;
//             const int WS_CLIPCHILDREN = 0x02000000;
//             const int WS_CLIPSIBLINGS = 0x04000000;
//             const int WS_TABSTOP = 0x00010000;
//             const int SS_NOTIFY = 0x00000100;
//
//             _width = Math.Max(1, (int)Bounds.Width);
//             _height = Math.Max(1, (int)Bounds.Height);
//
//             _hwnd = CreateWindowExW(
//                 0, "static", "SilkWebGpuHost",
//                 WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS | WS_TABSTOP | SS_NOTIFY,
//                 0, 0, _width, _height,
//                 parent.Handle, nint.Zero, nint.Zero, nint.Zero);
//
//             // NativeControlHost embeds a real HWND, so pointer events do not
//             // bubble through Avalonia. Subclass the child window to handle
//             // navigation input at the point where it is actually received.
//             _nativeWndProc = NativeWindowProc;
//             _previousWndProc = SetWindowLongPtr(_hwnd, GWLP_WNDPROC,
//                 Marshal.GetFunctionPointerForDelegate(_nativeWndProc));
//
//             InitWebGpu();
//             StartRenderingLoop();
//
//             return new PlatformHandle(_hwnd, "HWND");
//         }
//
//         return base.CreateNativeControlCore(parent);
//     }
//
//     protected override void DestroyNativeControlCore(IPlatformHandle control)
//     {
//         StopRenderingLoop();
//         CleanupWebGpu();
//
//         if (OperatingSystem.IsWindows() && _hwnd != nint.Zero)
//         {
//             if (_previousWndProc != nint.Zero)
//             {
//                 SetWindowLongPtr(_hwnd, GWLP_WNDPROC, _previousWndProc);
//                 _previousWndProc = nint.Zero;
//             }
//
//             DestroyWindow(_hwnd);
//             _hwnd = nint.Zero;
//         }
//
//         _nativeWndProc = null;
//
//         base.DestroyNativeControlCore(control);
//     }
//
//     protected override void OnSizeChanged(SizeChangedEventArgs e)
//     {
//         base.OnSizeChanged(e);
//         int width = Math.Max(1, (int)e.NewSize.Width);
//         int height = Math.Max(1, (int)e.NewSize.Height);
//
//         if (_hwnd != nint.Zero && OperatingSystem.IsWindows())
//         {
//             SetWindowPos(_hwnd, nint.Zero, 0, 0, width, height, 0x0004 /* SWP_NOZORDER */ | 0x0002 /* SWP_NOMOVE */);
//         }
//
//         lock (_gpuLock)
//         {
//             _width = width;
//             _height = height;
//
//             if (_surface != null && _device != null)
//                 ReconfigureSurface();
//         }
//     }
//
//     private void InitWebGpu()
//     {
//         _gpu = new WebGpuRuntime();
//
//         var hwndDesc = new SurfaceDescriptorFromWindowsHWND
//         {
//             Chain = new ChainedStruct { SType = SType.SurfaceDescriptorFromWindowsHwnd },
//             Hwnd = (void*)_hwnd,
//             Hinstance = (void*)Marshal.GetHINSTANCE(typeof(SilkViewportControl).Module)
//         };
//
//         var surfaceDesc = new SurfaceDescriptor
//         {
//             NextInChain = (ChainedStruct*)&hwndDesc
//         };
//
//         _surface = WebGpuApi.Wgpu.InstanceCreateSurface(WebGpuApi.Instance.UnsafeHandle, in surfaceDesc);
//         _adapterWrapper = new WebGpuAdapter(WebGpuApi, WebGpuSurface.FromNative((nint)_surface));
//         _adapter = _adapterWrapper.UnsafeHandle;
//         _deviceWrapper = _adapterWrapper.CreateDevice();
//         _device = _deviceWrapper.UnsafeHandle;
//         _queue = _deviceWrapper.GetUnsafeQueue();
//
//         WebGpuApi.ConfigureDebugCallback(_deviceWrapper);
//
//         GpuVendor = "WebGPU (WGPU / Native)";
//         GpuRenderer = "Hardware Accelerated (Vulkan / D3D12)";
//
//         _swapChainFormat = WebGpuApi.Wgpu.SurfaceGetPreferredFormat(_surface, _adapter);
//         if (_swapChainFormat == TextureFormat.Undefined)
//         {
//             _swapChainFormat = TextureFormat.Bgra8Unorm;
//         }
//
//         ReconfigureSurface();
//         InitBuffers();
//         InitPipelines();
//     }
//
//     private void ReconfigureSurface()
//     {
//         if (_surface == null || _device == null) return;
//
//         var surfaceConfig = new SurfaceConfiguration
//         {
//             Device = _device,
//             Width = (uint)_width,
//             Height = (uint)_height,
//             Format = _swapChainFormat,
//             PresentMode = PresentMode.Immediate, // Uncapped FPS
//             Usage = TextureUsage.RenderAttachment,
//             AlphaMode = CompositeAlphaMode.Auto
//         };
//
//         WebGpuApi.Wgpu.SurfaceConfigure(_surface, in surfaceConfig);
//
//         // Recreate Depth Texture
//         if (_depthTextureView != null) WebGpuApi.Wgpu.TextureViewRelease(_depthTextureView);
//         if (_depthTexture != null)
//         {
//             WebGpuApi.Wgpu.TextureDestroy(_depthTexture);
//             WebGpuApi.Wgpu.TextureRelease(_depthTexture);
//         }
//
//         var depthTextureDesc = new TextureDescriptor
//         {
//             Usage = TextureUsage.RenderAttachment,
//             Dimension = TextureDimension.Dimension2D,
//             Size = new Extent3D { Width = (uint)_width, Height = (uint)_height, DepthOrArrayLayers = 1 },
//             Format = TextureFormat.Depth24Plus,
//             MipLevelCount = 1,
//             SampleCount = 1
//         };
//
//         _depthTexture = WebGpuApi.Wgpu.DeviceCreateTexture(_device, in depthTextureDesc);
//         _depthTextureView = WebGpuApi.Wgpu.TextureCreateView(_depthTexture, null);
//     }
//
//     private void InitBuffers()
//     {
//         if (_device == null) return;
//
//         // 1. Cube Vertices (Pos 3 + Normal 3)
//         float[] cubeVertices = new float[]
//         {
//             // Front
//             -0.5f, -0.5f,  0.5f,  0f, 0f, 1f,   0.5f, -0.5f,  0.5f,  0f, 0f, 1f,   0.5f,  0.5f,  0.5f,  0f, 0f, 1f,  -0.5f,  0.5f,  0.5f,  0f, 0f, 1f,
//             // Back
//             -0.5f, -0.5f, -0.5f,  0f, 0f,-1f,  -0.5f,  0.5f, -0.5f,  0f, 0f,-1f,   0.5f,  0.5f, -0.5f,  0f, 0f,-1f,   0.5f, -0.5f, -0.5f,  0f, 0f,-1f,
//             // Top
//             -0.5f,  0.5f, -0.5f,  0f, 1f, 0f,  -0.5f,  0.5f,  0.5f,  0f, 1f, 0f,   0.5f,  0.5f,  0.5f,  0f, 1f, 0f,   0.5f,  0.5f, -0.5f,  0f, 1f, 0f,
//             // Bottom
//             -0.5f, -0.5f, -0.5f,  0f,-1f, 0f,   0.5f, -0.5f, -0.5f,  0f,-1f, 0f,   0.5f, -0.5f,  0.5f,  0f,-1f, 0f,  -0.5f, -0.5f,  0.5f,  0f,-1f, 0f,
//             // Right
//              0.5f, -0.5f, -0.5f,  1f, 0f, 0f,   0.5f,  0.5f, -0.5f,  1f, 0f, 0f,   0.5f,  0.5f,  0.5f,  1f, 0f, 0f,   0.5f, -0.5f,  0.5f,  1f, 0f, 0f,
//             // Left
//             -0.5f, -0.5f, -0.5f, -1f, 0f, 0f,  -0.5f, -0.5f,  0.5f, -1f, 0f, 0f,  -0.5f,  0.5f,  0.5f, -1f, 0f, 0f,  -0.5f,  0.5f, -0.5f, -1f, 0f, 0f,
//         };
//
//         ushort[] cubeIndices = new ushort[]
//         {
//              0,  1,  2,  2,  3,  0,
//              4,  5,  6,  6,  7,  4,
//              8,  9, 10, 10, 11,  8,
//             12, 13, 14, 14, 15, 12,
//             16, 17, 18, 18, 19, 16,
//             20, 21, 22, 22, 23, 20
//         };
//
//         _cubeVbo = CreateGpuBuffer(cubeVertices, BufferUsage.Vertex);
//         _cubeEbo = CreateGpuBuffer(cubeIndices, BufferUsage.Index);
//         ushort[] cubeWireframeIndices = new ushort[]
//         {
//             0, 1, 1, 2, 2, 3, 3, 0,
//             4, 5, 5, 6, 6, 7, 7, 4,
//             8, 9, 9, 10, 10, 11, 11, 8,
//             12, 13, 13, 14, 14, 15, 15, 12,
//             16, 17, 17, 18, 18, 19, 19, 16,
//             20, 21, 21, 22, 22, 23, 23, 20
//         };
//         _cubeWireframeEbo = CreateGpuBuffer(cubeWireframeIndices, BufferUsage.Index);
//
//         // 2. Pyramid Vertices
//         float[] pyramidVertices = new float[]
//         {
//             // Front face
//              0.0f,  0.75f, 0.0f,  0.0f, 0.447f, 0.894f,
//             -0.5f, -0.5f,  0.5f,  0.0f, 0.447f, 0.894f,
//              0.5f, -0.5f,  0.5f,  0.0f, 0.447f, 0.894f,
//             // Right face
//              0.0f,  0.75f, 0.0f,  0.894f, 0.447f, 0.0f,
//              0.5f, -0.5f,  0.5f,  0.894f, 0.447f, 0.0f,
//              0.5f, -0.5f, -0.5f,  0.894f, 0.447f, 0.0f,
//             // Back face
//              0.0f,  0.75f, 0.0f,  0.0f, 0.447f, -0.894f,
//              0.5f, -0.5f, -0.5f,  0.0f, 0.447f, -0.894f,
//             -0.5f, -0.5f, -0.5f,  0.0f, 0.447f, -0.894f,
//             // Left face
//              0.0f,  0.75f, 0.0f, -0.894f, 0.447f, 0.0f,
//             -0.5f, -0.5f, -0.5f, -0.894f, 0.447f, 0.0f,
//             -0.5f, -0.5f,  0.5f, -0.894f, 0.447f, 0.0f,
//             // Bottom face
//             -0.5f, -0.5f, -0.5f,  0.0f, -1.0f, 0.0f,
//              0.5f, -0.5f, -0.5f,  0.0f, -1.0f, 0.0f,
//              0.5f, -0.5f,  0.5f,  0.0f, -1.0f, 0.0f,
//             -0.5f, -0.5f,  0.5f,  0.0f, -1.0f, 0.0f
//         };
//
//         ushort[] pyramidIndices = new ushort[]
//         {
//             0, 1, 2,
//             3, 4, 5,
//             6, 7, 8,
//             9, 10, 11,
//             12, 13, 14, 14, 15, 12
//         };
//
//         _pyramidVbo = CreateGpuBuffer(pyramidVertices, BufferUsage.Vertex);
//         _pyramidEbo = CreateGpuBuffer(pyramidIndices, BufferUsage.Index);
//         ushort[] pyramidWireframeIndices = new ushort[]
//         {
//             0, 1, 1, 2, 2, 0,
//             3, 4, 4, 5, 5, 3,
//             6, 7, 7, 8, 8, 6,
//             9, 10, 10, 11, 11, 9,
//             12, 13, 13, 14, 14, 15, 15, 12
//         };
//         _pyramidWireframeEbo = CreateGpuBuffer(pyramidWireframeIndices, BufferUsage.Index);
//
//         // 3. Grid Quad Vertices
//         float[] gridVertices = new float[]
//         {
//              1.0f,  1.0f, 0.0f,
//             -1.0f, -1.0f, 0.0f,
//             -1.0f,  1.0f, 0.0f,
//              1.0f,  1.0f, 0.0f,
//              1.0f, -1.0f, 0.0f,
//             -1.0f, -1.0f, 0.0f
//         };
//
//         _gridVbo = CreateGpuBuffer(gridVertices, BufferUsage.Vertex);
//
//         // 4. Uniform Buffers
//         _gridUniformBuffer = CreateEmptyBuffer((ulong)sizeof(GridUniforms), BufferUsage.Uniform | BufferUsage.CopyDst);
//     }
//
//     private Buffer* CreateGpuBuffer<T>(T[] data, BufferUsage usage) where T : unmanaged
//     {
//         fixed (T* ptr = data)
//         {
//             var size = (ulong)(data.Length * sizeof(T));
//             var desc = new BufferDescriptor
//             {
//                 Usage = usage | BufferUsage.CopyDst,
//                 Size = size,
//                 MappedAtCreation = false
//             };
//             var buffer = WebGpuApi.Wgpu.DeviceCreateBuffer(_device!, in desc);
//             WebGpuApi.Wgpu.QueueWriteBuffer(_queue, buffer, 0, ptr, (nuint)size);
//             return buffer;
//         }
//     }
//
//     private Buffer* CreateEmptyBuffer(ulong size, BufferUsage usage)
//     {
//         var desc = new BufferDescriptor
//         {
//             Usage = usage,
//             Size = size,
//             MappedAtCreation = false
//         };
//         return WebGpuApi.Wgpu.DeviceCreateBuffer(_device!, in desc);
//     }
//
//     private MeshGpuResources GetMeshResources(SceneObject obj)
//     {
//         if (_meshResources.TryGetValue(obj, out MeshGpuResources? resources))
//         {
//             return resources;
//         }
//
//         Buffer* uniformBuffer = CreateEmptyBuffer((ulong)sizeof(MeshUniforms), BufferUsage.Uniform | BufferUsage.CopyDst);
//         var bindGroupEntry = new BindGroupEntry
//         {
//             Binding = 0,
//             Buffer = uniformBuffer,
//             Size = (ulong)sizeof(MeshUniforms)
//         };
//         var bindGroupDesc = new BindGroupDescriptor
//         {
//             Layout = _meshBindGroupLayout,
//             EntryCount = 1,
//             Entries = &bindGroupEntry
//         };
//
//         resources = new MeshGpuResources
//         {
//             UniformBuffer = WebGpuBuffer.FromNative((nint)uniformBuffer),
//             BindGroup = WebGpuBindGroup.FromNative((nint)WebGpuApi.Wgpu.DeviceCreateBindGroup(_device!, in bindGroupDesc))
//         };
//
//         Model? model = MeshRenderPass.GetModel(obj);
//         if (model != null)
//         {
//             foreach (ModelMesh mesh in model.Meshes)
//             {
//                 if (mesh.Positions.Count == 0 || mesh.Indices.Count == 0) continue;
//
//                 float[] vertices = new float[mesh.Positions.Count * 12];
//                 for (int i = 0; i < mesh.Positions.Count; i++)
//                 {
//                     Vector3 position = mesh.Positions[i];
//                     Vector3 normal = i < mesh.Normals.Count ? mesh.Normals[i] : Vector3.UnitY;
//                     Vector4 tangent = i < mesh.Tangents.Count ? mesh.Tangents[i] : new Vector4(Vector3.UnitX, 1f);
//                     Vector2 uv = i < mesh.TextureCoordinates.Count ? mesh.TextureCoordinates[i] : Vector2.Zero;
//                     int offset = i * 12;
//                     vertices[offset] = position.X;
//                     vertices[offset + 1] = position.Y;
//                     vertices[offset + 2] = position.Z;
//                     vertices[offset + 3] = normal.X;
//                     vertices[offset + 4] = normal.Y;
//                     vertices[offset + 5] = normal.Z;
//                     vertices[offset + 6] = tangent.X;
//                     vertices[offset + 7] = tangent.Y;
//                     vertices[offset + 8] = tangent.Z;
//                     vertices[offset + 9] = tangent.W;
//                     vertices[offset + 10] = uv.X;
//                     vertices[offset + 11] = uv.Y;
//                 }
//
//                 uint[] indices = mesh.Indices.Select(index => checked((uint)index)).ToArray();
//                 uint[] wireframeIndices = new uint[indices.Length * 2];
//                 for (int i = 0; i < indices.Length; i += 3)
//                 {
//                     int offset = i * 2;
//                     wireframeIndices[offset] = indices[i];
//                     wireframeIndices[offset + 1] = indices[i + 1];
//                     wireframeIndices[offset + 2] = indices[i + 1];
//                     wireframeIndices[offset + 3] = indices[i + 2];
//                     wireframeIndices[offset + 4] = indices[i + 2];
//                     wireframeIndices[offset + 5] = indices[i];
//                 }
//
//                 var gpuMesh = new ModelGpuMesh
//                 {
//                     VertexBuffer = WebGpuBuffer.FromNative((nint)CreateGpuBuffer(vertices, BufferUsage.Vertex)),
//                     VertexBufferSize = (ulong)(vertices.Length * sizeof(float)),
//                     IndexBuffer = WebGpuBuffer.FromNative((nint)CreateGpuBuffer(indices, BufferUsage.Index)),
//                     WireframeIndexBuffer = WebGpuBuffer.FromNative((nint)CreateGpuBuffer(wireframeIndices, BufferUsage.Index)),
//                     IndexCount = (uint)indices.Length,
//                     WireframeIndexCount = (uint)wireframeIndices.Length
//                 };
//                 CreateMaterialBindGroup(gpuMesh, model, mesh.MaterialIndex);
//                 resources.ModelMeshes.Add(gpuMesh);
//             }
//         }
//
//         _meshResources.Add(obj, resources);
//         return resources;
//     }
//
//     private void CreateMaterialBindGroup(ModelGpuMesh gpuMesh, Model model, int materialIndex)
//     {
//         if (_pbrMaterialBindGroupLayout == null) return;
//
//         ModelMaterial material = materialIndex >= 0 && materialIndex < model.Materials.Count
//             ? model.Materials[materialIndex]
//             : new ModelMaterial();
//         ModelTexture?[] textures =
//         [material.BaseColorTexture, material.NormalTexture, material.MetallicRoughnessTexture, null, null];
//
//         var samplerDesc = new SamplerDescriptor
//         {
//             AddressModeU = AddressMode.Repeat,
//             AddressModeV = AddressMode.Repeat,
//             AddressModeW = AddressMode.Repeat,
//             MagFilter = FilterMode.Linear,
//             MinFilter = FilterMode.Linear,
//             MipmapFilter = MipmapFilterMode.Linear,
//             LodMinClamp = 0,
//             LodMaxClamp = 32,
//             MaxAnisotropy = 1
//         };
//         Sampler* sampler = WebGpuApi.Wgpu.DeviceCreateSampler(_device, in samplerDesc);
//         gpuMesh.MaterialSampler = (nint)sampler;
//
//         for (int i = 0; i < textures.Length; i++)
//         {
//             ModelTexture texture = textures[i] ?? new ModelTexture
//             {
//                 Width = 1,
//                 Height = 1,
//                 Pixels = i == 1 ? [128, 128, 255, 255] : i == 2 ? [0, 255, 0, 255] : [255, 255, 255, 255]
//             };
//             GpuTextureResult gpuTexture = CreateGpuTexture(texture, i is 0 or 4);
//             gpuMesh.MaterialTextures[i] = (nint)gpuTexture.Texture;
//             gpuMesh.MaterialTextureViews[i] = (nint)gpuTexture.View;
//         }
//
//         var entries = stackalloc BindGroupEntry[6];
//         entries[0] = new BindGroupEntry { Binding = 0, Sampler = sampler };
//         for (int i = 0; i < 5; i++)
//             entries[i + 1] = new BindGroupEntry { Binding = (uint)(i + 1), TextureView = (TextureView*)gpuMesh.MaterialTextureViews[i] };
//
//         var desc = new BindGroupDescriptor { Layout = _pbrMaterialBindGroupLayout, EntryCount = 6, Entries = entries };
//         gpuMesh.MaterialBindGroup = WebGpuBindGroup.FromNative((nint)WebGpuApi.Wgpu.DeviceCreateBindGroup(_device, in desc));
//     }
//
//     private struct GpuTextureResult
//     {
//         public Texture* Texture;
//         public TextureView* View;
//     }
//
//     private GpuTextureResult CreateGpuTexture(ModelTexture source, bool srgb)
//     {
//         var textureDesc = new TextureDescriptor
//         {
//             Usage = TextureUsage.CopyDst | TextureUsage.TextureBinding,
//             Dimension = TextureDimension.Dimension2D,
//             Size = new Extent3D { Width = (uint)source.Width, Height = (uint)source.Height, DepthOrArrayLayers = 1 },
//             Format = srgb ? TextureFormat.Rgba8UnormSrgb : TextureFormat.Rgba8Unorm,
//             MipLevelCount = 1,
//             SampleCount = 1
//         };
//         Texture* texture = WebGpuApi.Wgpu.DeviceCreateTexture(_device, in textureDesc);
//         var copy = new ImageCopyTexture { Texture = texture, MipLevel = 0, Aspect = TextureAspect.All };
//         var layout = new TextureDataLayout { Offset = 0, BytesPerRow = checked((uint)(source.Width * 4)), RowsPerImage = checked((uint)source.Height) };
//         var extent = new Extent3D { Width = (uint)source.Width, Height = (uint)source.Height, DepthOrArrayLayers = 1 };
//         fixed (byte* pixels = source.Pixels)
//             WebGpuApi.Wgpu.QueueWriteTexture(_queue, in copy, pixels, (nuint)source.Pixels.Length, in layout, in extent);
//         return new GpuTextureResult { Texture = texture, View = WebGpuApi.Wgpu.TextureCreateView(texture, null) };
//     }
//
//     private void CleanupMeshResources()
//     {
//         Scene? scene = _renderScene;
//         if (scene == null) return;
//
//         List<SceneObject>? removed = null;
//         foreach (SceneObject obj in _meshResources.Keys)
//         {
//             if (obj.Renderer?.GameObject is not { } gameObject || !scene.GameObjects.Contains(gameObject))
//             {
//                 (removed ??= new List<SceneObject>()).Add(obj);
//             }
//         }
//
//         if (removed == null) return;
//
//         foreach (SceneObject obj in removed)
//         {
//             ReleaseMeshResources(_meshResources[obj]);
//             _meshResources.Remove(obj);
//         }
//     }
//
//     private void ReleaseMeshResources(MeshGpuResources resources)
//     {
//         foreach (ModelGpuMesh mesh in resources.ModelMeshes)
//         {
//             if (mesh.MaterialBindGroup.NativeHandle != nint.Zero)
//                 WebGpuApi.Wgpu.BindGroupRelease((BindGroup*)mesh.MaterialBindGroup.NativeHandle);
//             if (mesh.MaterialSampler != nint.Zero)
//                 WebGpuApi.Wgpu.SamplerRelease((Sampler*)mesh.MaterialSampler);
//             for (int i = 0; i < mesh.MaterialTextures.Length; i++)
//             {
//                 if (mesh.MaterialTextureViews[i] != nint.Zero)
//                     WebGpuApi.Wgpu.TextureViewRelease((TextureView*)mesh.MaterialTextureViews[i]);
//                 if (mesh.MaterialTextures[i] != nint.Zero)
//                 {
//                     Texture* texture = (Texture*)mesh.MaterialTextures[i];
//                     WebGpuApi.Wgpu.TextureDestroy(texture);
//                     WebGpuApi.Wgpu.TextureRelease(texture);
//                 }
//             }
//             ReleaseBuffer(mesh.VertexBuffer);
//             ReleaseBuffer(mesh.IndexBuffer);
//             ReleaseBuffer(mesh.WireframeIndexBuffer);
//         }
//
//         if (resources.BindGroup.NativeHandle != nint.Zero)
//         {
//             WebGpuApi.Wgpu.BindGroupRelease((BindGroup*)resources.BindGroup.NativeHandle);
//         }
//
//         if (resources.UniformBuffer.NativeHandle != nint.Zero)
//         {
//             WebGpuApi.Wgpu.BufferDestroy((Buffer*)resources.UniformBuffer.NativeHandle);
//             WebGpuApi.Wgpu.BufferRelease((Buffer*)resources.UniformBuffer.NativeHandle);
//         }
//     }
//
//     private void ReleaseBuffer(WebGpuBuffer bufferHandle)
//     {
//         if (bufferHandle.NativeHandle != nint.Zero)
//         {
//             Buffer* buffer = (Buffer*)bufferHandle.NativeHandle;
//             WebGpuApi.Wgpu.BufferDestroy(buffer);
//             WebGpuApi.Wgpu.BufferRelease(buffer);
//         }
//     }
//
//     private void InitPipelines()
//     {
//         if (_device == null) return;
//
//         var meshShader = Shader.Load("Shaders/Surface/Standard.wgsl");
//         _meshShader = meshShader;
//         var pbrShader = Shader.Load("Shaders/Surface/StandardPbr.wgsl");
//         var gridShader = Shader.Load("Shaders/Editor/Grid.wgsl");
//         _meshShaderModule = CreateShaderModule(meshShader);
//         _pbrShaderModule = CreateShaderModule(pbrShader);
//         _gridShaderModule = CreateShaderModule(gridShader);
//
//         // BindGroupLayout for Mesh
//         var meshLayoutEntry = new BindGroupLayoutEntry
//         {
//             Binding = 0,
//             Visibility = ShaderStage.Vertex | ShaderStage.Fragment,
//             Buffer = new BufferBindingLayout { Type = BufferBindingType.Uniform }
//         };
//         var meshLayoutDesc = new BindGroupLayoutDescriptor { EntryCount = 1, Entries = &meshLayoutEntry };
//         _meshBindGroupLayout = WebGpuApi.Wgpu.DeviceCreateBindGroupLayout(_device, in meshLayoutDesc);
//
//         var materialEntries = stackalloc BindGroupLayoutEntry[6];
//         materialEntries[0] = new BindGroupLayoutEntry
//         {
//             Binding = 0,
//             Visibility = ShaderStage.Fragment,
//             Sampler = new SamplerBindingLayout { Type = SamplerBindingType.Filtering }
//         };
//         for (int i = 0; i < 5; i++)
//         {
//             materialEntries[i + 1] = new BindGroupLayoutEntry
//             {
//                 Binding = (uint)(i + 1),
//                 Visibility = ShaderStage.Fragment,
//                 Texture = new TextureBindingLayout
//                 {
//                     SampleType = TextureSampleType.Float,
//                     ViewDimension = TextureViewDimension.Dimension2D,
//                     Multisampled = false
//                 }
//             };
//         }
//         var materialLayoutDesc = new BindGroupLayoutDescriptor { EntryCount = 6, Entries = materialEntries };
//         _pbrMaterialBindGroupLayout = WebGpuApi.Wgpu.DeviceCreateBindGroupLayout(_device, in materialLayoutDesc);
//
//         // PipelineLayout for Mesh
//         var meshLayoutLocal = _meshBindGroupLayout;
//         var meshPipelineLayoutDesc = new PipelineLayoutDescriptor { BindGroupLayoutCount = 1, BindGroupLayouts = &meshLayoutLocal };
//         var meshPipelineLayout = WebGpuApi.Wgpu.DeviceCreatePipelineLayout(_device, in meshPipelineLayoutDesc);
//
//         // Vertex Attributes for Mesh (Pos 3 + Normal 3)
//         var vertexAttribs = stackalloc VertexAttribute[2];
//         vertexAttribs[0] = new VertexAttribute { Format = VertexFormat.Float32x3, Offset = 0, ShaderLocation = 0 };
//         vertexAttribs[1] = new VertexAttribute { Format = VertexFormat.Float32x3, Offset = 3 * sizeof(float), ShaderLocation = 1 };
//
//         var vertexBufferLayout = new VertexBufferLayout
//         {
//             ArrayStride = 6 * sizeof(float),
//             StepMode = VertexStepMode.Vertex,
//             AttributeCount = 2,
//             Attributes = vertexAttribs
//         };
//
//         var meshBlendState = new BlendState
//         {
//             Color = new BlendComponent
//             {
//                 SrcFactor = BlendFactor.SrcAlpha,
//                 DstFactor = BlendFactor.OneMinusSrcAlpha,
//                 Operation = BlendOperation.Add
//             },
//             Alpha = new BlendComponent
//             {
//                 SrcFactor = BlendFactor.One,
//                 DstFactor = BlendFactor.OneMinusSrcAlpha,
//                 Operation = BlendOperation.Add
//             }
//         };
//
//         var colorTarget = new ColorTargetState
//         {
//             Format = _swapChainFormat,
//             // Mesh materials use the alpha supplied by the inspector.  The
//             // default (opaque) value remains unchanged, while lower values
//             // blend the object with what was rendered behind it.
//             Blend = &meshBlendState,
//             WriteMask = ColorWriteMask.All
//         };
//
//         var fsEntryPoint = Marshal.StringToHGlobalAnsi(meshShader.GetEntryPoint("fs_main").Name);
//         var vsEntryPoint = Marshal.StringToHGlobalAnsi(meshShader.GetEntryPoint("vs_main").Name);
//
//         var fragmentState = new FragmentState
//         {
//             Module = _meshShaderModule,
//             EntryPoint = (byte*)fsEntryPoint.ToPointer(),
//             TargetCount = 1,
//             Targets = &colorTarget
//         };
//
//         var depthStencilState = new DepthStencilState
//         {
//             Format = TextureFormat.Depth24Plus,
//             DepthWriteEnabled = true,
//             DepthCompare = CompareFunction.Less,
//             StencilFront = new StencilFaceState { Compare = CompareFunction.Always },
//             StencilBack = new StencilFaceState { Compare = CompareFunction.Always }
//         };
//
//         var meshPipelineDesc = new RenderPipelineDescriptor
//         {
//             Layout = meshPipelineLayout,
//             Vertex = new VertexState
//             {
//                 Module = _meshShaderModule,
//                 EntryPoint = (byte*)vsEntryPoint.ToPointer(),
//                 BufferCount = 1,
//                 Buffers = &vertexBufferLayout
//             },
//             Primitive = new PrimitiveState
//             {
//                 Topology = PrimitiveTopology.TriangleList,
//                 FrontFace = FrontFace.Ccw,
//                 CullMode = CullMode.None
//             },
//             DepthStencil = &depthStencilState,
//             Multisample = new MultisampleState { Count = 1, Mask = 0xFFFFFFFF },
//             Fragment = &fragmentState
//         };
//
//         _meshPipeline = WebGpuApi.Wgpu.DeviceCreateRenderPipeline(_device, in meshPipelineDesc);
//
//         var pbrLayoutHandles = stackalloc BindGroupLayout*[2];
//         pbrLayoutHandles[0] = _meshBindGroupLayout;
//         pbrLayoutHandles[1] = _pbrMaterialBindGroupLayout;
//         var pbrPipelineLayoutDesc = new PipelineLayoutDescriptor { BindGroupLayoutCount = 2, BindGroupLayouts = pbrLayoutHandles };
//         var pbrPipelineLayout = WebGpuApi.Wgpu.DeviceCreatePipelineLayout(_device, in pbrPipelineLayoutDesc);
//         var pbrAttributes = stackalloc VertexAttribute[4];
//         pbrAttributes[0] = new VertexAttribute { Format = VertexFormat.Float32x3, Offset = 0, ShaderLocation = 0 };
//         pbrAttributes[1] = new VertexAttribute { Format = VertexFormat.Float32x3, Offset = 3 * sizeof(float), ShaderLocation = 1 };
//         pbrAttributes[2] = new VertexAttribute { Format = VertexFormat.Float32x4, Offset = 6 * sizeof(float), ShaderLocation = 2 };
//         pbrAttributes[3] = new VertexAttribute { Format = VertexFormat.Float32x2, Offset = 10 * sizeof(float), ShaderLocation = 3 };
//         var pbrBufferLayout = new VertexBufferLayout { ArrayStride = 12 * sizeof(float), StepMode = VertexStepMode.Vertex, AttributeCount = 4, Attributes = pbrAttributes };
//         var pbrFsEntry = Marshal.StringToHGlobalAnsi(pbrShader.GetEntryPoint("fs_main").Name);
//         var pbrVsEntry = Marshal.StringToHGlobalAnsi(pbrShader.GetEntryPoint("vs_main").Name);
//         var pbrFragment = new FragmentState
//         {
//             Module = _pbrShaderModule,
//             EntryPoint = (byte*)pbrFsEntry.ToPointer(),
//             TargetCount = 1,
//             Targets = &colorTarget
//         };
//         var pbrPipelineDesc = new RenderPipelineDescriptor
//         {
//             Layout = pbrPipelineLayout,
//             Vertex = new VertexState { Module = _pbrShaderModule, EntryPoint = (byte*)pbrVsEntry.ToPointer(), BufferCount = 1, Buffers = &pbrBufferLayout },
//             Primitive = new PrimitiveState { Topology = PrimitiveTopology.TriangleList, FrontFace = FrontFace.Ccw, CullMode = CullMode.None },
//             DepthStencil = &depthStencilState,
//             Multisample = new MultisampleState { Count = 1, Mask = 0xFFFFFFFF },
//             Fragment = &pbrFragment
//         };
//         _pbrPipeline = WebGpuApi.Wgpu.DeviceCreateRenderPipeline(_device, in pbrPipelineDesc);
//         var pbrTransparentDepth = depthStencilState;
//         pbrTransparentDepth.DepthWriteEnabled = false;
//         pbrPipelineDesc.DepthStencil = &pbrTransparentDepth;
//         _transparentPbrPipeline = WebGpuApi.Wgpu.DeviceCreateRenderPipeline(_device, in pbrPipelineDesc);
//
//         // Transparent meshes must still test against opaque geometry, but
//         // they must not write their own depth. Otherwise an object with
//         // alpha 0 would remain an invisible occluder for objects behind it.
//         var transparentDepthStencilState = depthStencilState;
//         transparentDepthStencilState.DepthWriteEnabled = false;
//         var transparentMeshPipelineDesc = meshPipelineDesc;
//         transparentMeshPipelineDesc.DepthStencil = &transparentDepthStencilState;
//         _transparentMeshPipeline = WebGpuApi.Wgpu.DeviceCreateRenderPipeline(_device, in transparentMeshPipelineDesc);
//
//         // This pass writes only the selected mesh's depth.  It is used just
//         // before the outline so the expanded back-face shell is hidden inside
//         // the selected object, while still being able to overlay other scene
//         // objects.
//         var selectionDepthStencilState = depthStencilState;
//         selectionDepthStencilState.DepthCompare = CompareFunction.Always;
//         var selectionDepthColorTarget = colorTarget;
//         selectionDepthColorTarget.WriteMask = ColorWriteMask.None;
//         var selectionDepthFragmentState = fragmentState;
//         selectionDepthFragmentState.Targets = &selectionDepthColorTarget;
//         var selectionDepthPipelineDesc = meshPipelineDesc;
//         selectionDepthPipelineDesc.DepthStencil = &selectionDepthStencilState;
//         selectionDepthPipelineDesc.Fragment = &selectionDepthFragmentState;
//         _selectionDepthPipeline = WebGpuApi.Wgpu.DeviceCreateRenderPipeline(_device, in selectionDepthPipelineDesc);
//
//         // WebGPU has no polygon-mode switch. Wireframe rendering therefore
//         // uses the same shader and vertex layout with a line-list pipeline;
//         // the dedicated index buffers contain the edges of each mesh face.
//         var wireframePipelineDesc = meshPipelineDesc;
//         wireframePipelineDesc.Primitive = new PrimitiveState
//         {
//             Topology = PrimitiveTopology.LineList,
//             FrontFace = FrontFace.Ccw,
//             CullMode = CullMode.None
//         };
//         var outlineDepthStencilState = depthStencilState;
//         outlineDepthStencilState.DepthWriteEnabled = false;
//         // The selected object's depth mask is written immediately before this
//         // pass, so LessEqual keeps the outline on the silhouette instead of
//         // filling the object's faces with the outline color.
//         outlineDepthStencilState.DepthCompare = CompareFunction.LessEqual;
//         wireframePipelineDesc.DepthStencil = &outlineDepthStencilState;
//         _wireframePipeline = WebGpuApi.Wgpu.DeviceCreateRenderPipeline(_device, in wireframePipelineDesc);
//         WebGpuRenderPipeline meshPipeline = MeshRenderPass.CreatePipeline((nint)_meshPipeline);
//         WebGpuRenderPipeline pbrPipeline = MeshRenderPass.CreatePipeline((nint)_pbrPipeline);
//         WebGpuRenderPipeline transparentPbrPipeline = MeshRenderPass.CreatePipeline((nint)_transparentPbrPipeline);
//         WebGpuRenderPipeline transparentMeshPipeline = MeshRenderPass.CreatePipeline((nint)_transparentMeshPipeline);
//         WebGpuRenderPipeline wireframePipeline = MeshRenderPass.CreatePipeline((nint)_wireframePipeline);
//         _opaqueMeshPass = new MeshRenderPass(meshPipeline, MeshRenderPassMode.Opaque, pbrPipeline);
//         _transparentMeshPass = new MeshRenderPass(transparentMeshPipeline, MeshRenderPassMode.Transparent, transparentPbrPipeline);
//         _wireframeMeshPass = new MeshRenderPass(wireframePipeline, MeshRenderPassMode.Wireframe);
//
//         var outlineFragmentState = fragmentState;
//         var outlineFsEntryPoint = Marshal.StringToHGlobalAnsi(meshShader.GetEntryPoint("fs_outline").Name);
//         outlineFragmentState.EntryPoint = (byte*)outlineFsEntryPoint.ToPointer();
//         var outlineVsEntryPoint = Marshal.StringToHGlobalAnsi(meshShader.GetEntryPoint("vs_outline").Name);
//         var outlineVertexState = new VertexState
//         {
//             Module = _meshShaderModule,
//             EntryPoint = (byte*)outlineVsEntryPoint.ToPointer(),
//             BufferCount = 1,
//             Buffers = &vertexBufferLayout
//         };
//         var outlinePipelineDesc = meshPipelineDesc;
//         outlinePipelineDesc.Vertex = outlineVertexState;
//         outlinePipelineDesc.Primitive = new PrimitiveState
//         {
//             Topology = PrimitiveTopology.TriangleList,
//             FrontFace = FrontFace.Ccw,
//             CullMode = CullMode.Front
//         };
//         outlinePipelineDesc.DepthStencil = &outlineDepthStencilState;
//         outlinePipelineDesc.Fragment = &outlineFragmentState;
//         _outlinePipeline = WebGpuApi.Wgpu.DeviceCreateRenderPipeline(_device, in outlinePipelineDesc);
//
//
//         // BindGroupLayout for Grid
//         var gridLayoutEntry = new BindGroupLayoutEntry
//         {
//             Binding = 0,
//             Visibility = ShaderStage.Vertex | ShaderStage.Fragment,
//             Buffer = new BufferBindingLayout { Type = BufferBindingType.Uniform }
//         };
//         var gridLayoutDesc = new BindGroupLayoutDescriptor { EntryCount = 1, Entries = &gridLayoutEntry };
//         _gridBindGroupLayout = WebGpuApi.Wgpu.DeviceCreateBindGroupLayout(_device, in gridLayoutDesc);
//
//         var gridBindGroupEntry = new BindGroupEntry
//         {
//             Binding = 0,
//             Buffer = _gridUniformBuffer,
//             Size = (ulong)sizeof(GridUniforms)
//         };
//         var gridBindGroupDesc = new BindGroupDescriptor
//         {
//             Layout = _gridBindGroupLayout,
//             EntryCount = 1,
//             Entries = &gridBindGroupEntry
//         };
//         _gridBindGroup = WebGpuApi.Wgpu.DeviceCreateBindGroup(_device, in gridBindGroupDesc);
//
//         var gridLayoutLocal = _gridBindGroupLayout;
//         var gridPipelineLayoutDesc = new PipelineLayoutDescriptor { BindGroupLayoutCount = 1, BindGroupLayouts = &gridLayoutLocal };
//         var gridPipelineLayout = WebGpuApi.Wgpu.DeviceCreatePipelineLayout(_device, in gridPipelineLayoutDesc);
//
//         var gridAttrib = new VertexAttribute { Format = VertexFormat.Float32x3, Offset = 0, ShaderLocation = 0 };
//         var gridBufferLayout = new VertexBufferLayout
//         {
//             ArrayStride = 3 * sizeof(float),
//             StepMode = VertexStepMode.Vertex,
//             AttributeCount = 1,
//             Attributes = &gridAttrib
//         };
//
//         var blendState = new BlendState
//         {
//             Color = new BlendComponent { SrcFactor = BlendFactor.SrcAlpha, DstFactor = BlendFactor.OneMinusSrcAlpha, Operation = BlendOperation.Add },
//             Alpha = new BlendComponent { SrcFactor = BlendFactor.One, DstFactor = BlendFactor.OneMinusSrcAlpha, Operation = BlendOperation.Add }
//         };
//
//         var gridColorTarget = new ColorTargetState
//         {
//             Format = _swapChainFormat,
//             Blend = &blendState,
//             WriteMask = ColorWriteMask.All
//         };
//
//         var gridDepthStencilState = new DepthStencilState
//         {
//             Format = TextureFormat.Depth24Plus,
//             // The grid is rendered after the meshes. It can test mesh depth,
//             // but must not replace it or write depth between grid lines.
//             DepthWriteEnabled = false,
//             DepthCompare = CompareFunction.LessEqual,
//             StencilFront = new StencilFaceState { Compare = CompareFunction.Always },
//             StencilBack = new StencilFaceState { Compare = CompareFunction.Always }
//         };
//
//         var gridFsEntryPoint = Marshal.StringToHGlobalAnsi(gridShader.GetEntryPoint("fs_main").Name);
//         var gridVsEntryPoint = Marshal.StringToHGlobalAnsi(gridShader.GetEntryPoint("vs_main").Name);
//
//         var gridFragmentState = new FragmentState
//         {
//             Module = _gridShaderModule,
//             EntryPoint = (byte*)gridFsEntryPoint.ToPointer(),
//             TargetCount = 1,
//             Targets = &gridColorTarget
//         };
//
//         var gridPipelineDesc = new RenderPipelineDescriptor
//         {
//             Layout = gridPipelineLayout,
//             Vertex = new VertexState
//             {
//                 Module = _gridShaderModule,
//                 EntryPoint = (byte*)gridVsEntryPoint.ToPointer(),
//                 BufferCount = 1,
//                 Buffers = &gridBufferLayout
//             },
//             Primitive = new PrimitiveState
//             {
//                 Topology = PrimitiveTopology.TriangleList,
//                 FrontFace = FrontFace.Ccw,
//                 CullMode = CullMode.None
//             },
//             DepthStencil = &gridDepthStencilState,
//             Multisample = new MultisampleState { Count = 1, Mask = 0xFFFFFFFF },
//             Fragment = &gridFragmentState
//         };
//
//         _gridPipeline = WebGpuApi.Wgpu.DeviceCreateRenderPipeline(_device, in gridPipelineDesc);
//     }
//
//     private ShaderModule* CreateShaderModule(Shader shader)
//     {
//         var codePtr = Marshal.StringToHGlobalAnsi(shader.Source);
//         var wgslDescriptor = new ShaderModuleWGSLDescriptor
//         {
//             Code = (byte*)codePtr.ToPointer()
//         };
//         wgslDescriptor.Chain.SType = SType.ShaderModuleWgslDescriptor;
//
//         var descriptor = new ShaderModuleDescriptor { NextInChain = (ChainedStruct*)&wgslDescriptor };
//         var module = WebGpuApi.Wgpu.DeviceCreateShaderModule(_device!, in descriptor);
//         Marshal.FreeHGlobal(codePtr);
//         return module;
//     }
//
//     private void StartRenderingLoop()
//     {
//         StopRenderingLoop();
//         _renderScene = Scene;
//
//         _renderCancellation = new CancellationTokenSource();
//         CancellationToken cancellationToken = _renderCancellation.Token;
//         _renderThread = new Thread(() =>
//         {
//             while (!cancellationToken.IsCancellationRequested)
//             {
//                 lock (_gpuLock)
//                 {
//                     if (_surface != null && _device != null)
//                         RenderFrame();
//                 }
//
//                 Thread.Yield();
//             }
//         })
//         {
//             IsBackground = true,
//             Name = "Crowbar Viewport Render"
//         };
//         _renderThread.Start();
//     }
//
//     private void StopRenderingLoop()
//     {
//         CancellationTokenSource? cancellation = _renderCancellation;
//         _renderCancellation = null;
//         cancellation?.Cancel();
//
//         Thread? renderThread = _renderThread;
//         _renderThread = null;
//         if (renderThread != null && renderThread != Thread.CurrentThread)
//             renderThread.Join();
//
//         cancellation?.Dispose();
//         _renderScene = null;
//     }
//
//     private void RenderFrame()
//     {
//         if (_surface == null || _device == null || _depthTextureView == null) return;
//         Scene? scene = _renderScene;
//
//         // 1. Timing & FPS
//         double currentTime = _stopwatch.Elapsed.TotalSeconds;
//         double deltaTime = currentTime - _lastFrameTime;
//         _lastFrameTime = currentTime;
//
//         UpdateKeyboardMovement((float)Math.Clamp(deltaTime, 0.0, 0.1));
//         scene?.UpdateCameraPositionSmoothing((float)deltaTime);
//         scene?.Update((float)deltaTime);
//
//         _frameCount++;
//         _fpsTimer += deltaTime;
//         if (_fpsTimer >= 0.5)
//         {
//             int fps = (int)(_frameCount / _fpsTimer);
//             float frameTimeMs = (float)((_fpsTimer / _frameCount) * 1000.0);
//             _frameCount = 0;
//             _fpsTimer = 0;
//
//             Dispatcher.UIThread.Post(() =>
//             {
//                 Fps = fps;
//                 FrameTimeMs = frameTimeMs;
//             });
//         }
//
//         // 2. Matrices & Camera Setup
//         float yawRad = MathF.PI / 180f * (scene?.CameraYaw ?? 45f);
//         float pitchRad = MathF.PI / 180f * (scene?.CameraPitch ?? 30f);
//         Vector3 eye = new(
//             scene?.CameraPositionX ?? 4.242641f,
//             scene?.CameraPositionY ?? 3.0f,
//             scene?.CameraPositionZ ?? 4.242641f);
//         Vector3 forward = GetCameraForward(yawRad, pitchRad);
//
//         Matrix4x4 view = Matrix4x4.CreateLookAt(eye, eye + forward, Vector3.UnitY);
//         Matrix4x4 proj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4f, (float)_width / _height, 0.1f, 100f);
//
//         Matrix4x4.Invert(view, out Matrix4x4 viewInv);
//         Matrix4x4.Invert(proj, out Matrix4x4 projInv);
//
//         // 3. Acquire Surface Texture
//         SurfaceTexture surfaceTexture;
//         WebGpuApi.Wgpu.SurfaceGetCurrentTexture(_surface, &surfaceTexture);
//         if (surfaceTexture.Status != SurfaceGetCurrentTextureStatus.Success || surfaceTexture.Texture == null) return;
//
//         TextureView* targetView = WebGpuApi.Wgpu.TextureCreateView(surfaceTexture.Texture, null);
//         using CommandList commandList = _deviceWrapper!.CreateCommandList();
//         using RenderPass renderPass = commandList.BeginRenderPass(new RenderPassDescription
//         {
//             Color = new ColorAttachment
//             {
//                 View = WebGpuTextureView.FromNative((nint)targetView),
//                 LoadOp = RenderAttachmentLoadOp.Clear,
//                 StoreOp = RenderAttachmentStoreOp.Store,
//                 ClearColor = new Vector4(0.12f, 0.12f, 0.14f, 1.0f)
//             },
//             Depth = new DepthAttachment
//             {
//                 View = WebGpuTextureView.FromNative((nint)_depthTextureView),
//                 LoadOp = RenderAttachmentLoadOp.Clear,
//                 StoreOp = RenderAttachmentStoreOp.Store,
//                 ClearValue = 1.0f
//             }
//         });
//
//         // Prepare the grid uniforms. The grid itself is drawn after the scene
//         // so mesh depth is already available to its depth test.
//         // System.Numerics uses row vectors. WGSL interprets the same memory
//         // as column-major matrices, which is exactly the transpose needed for
//         // `matrix * vector` in the shader.
//         GridUniforms gridUniforms = Grid.CreateUniforms(view, proj, viewInv, projInv);
//         WebGpuApi.WriteBuffer(WebGpuQueue.FromNative((nint)_queue),
//             WebGpuBuffer.FromNative((nint)_gridUniformBuffer), in gridUniforms);
//
//         // A. Draw Scene Objects
//         bool wireframe = scene?.IsWireframe == true;
//
//         Vector3 lightDir = Vector3.Normalize(new Vector3(0.5f, 1.0f, 0.7f));
//         SelectionRenderData? selection = null;
//
//         if (scene != null)
//         {
//             CleanupMeshResources();
//
//             var visibleObjects = scene.GameObjects
//                 .Select(gameObject => gameObject.ModelRenderer?.SceneObject)
//                 .Where(obj => obj is { IsVisible: true })
//                 .Cast<SceneObject>()
//                 .ToList();
//
//             var meshContext = new MeshRenderContext
//             {
//                 Runtime = WebGpuApi,
//                 Pass = renderPass,
//                 Queue = WebGpuQueue.FromNative((nint)_queue),
//                 View = view,
//                 Proj = proj,
//                 LightDirection = lightDir,
//                 CameraPosition = eye,
//                 CubeVertexBuffer = WebGpuBuffer.FromNative((nint)_cubeVbo),
//                 CubeIndexBuffer = WebGpuBuffer.FromNative((nint)_cubeEbo),
//                 CubeWireframeIndexBuffer = WebGpuBuffer.FromNative((nint)_cubeWireframeEbo),
//                 PyramidVertexBuffer = WebGpuBuffer.FromNative((nint)_pyramidVbo),
//                 PyramidIndexBuffer = WebGpuBuffer.FromNative((nint)_pyramidEbo),
//                 PyramidWireframeIndexBuffer = WebGpuBuffer.FromNative((nint)_pyramidWireframeEbo),
//                 GetResources = GetMeshResources,
//                 GetColor = GetMaterialColor,
//                 GetLightDirection = GetMaterialLightDirection,
//                 Wireframe = wireframe
//             };
//
//             if (wireframe)
//             {
//                 _wireframeMeshPass!.Execute(meshContext, visibleObjects);
//             }
//             else
//             {
//                 _opaqueMeshPass!.Execute(meshContext, visibleObjects);
//                 _transparentMeshPass!.Execute(meshContext, visibleObjects);
//             }
//
//             selection = meshContext.Selection;
//         }
//
//         // B. Draw Infinite Grid. It uses depth testing without writing depth,
//         // and the shader discards the space between lines.
//         renderPass.SetPipeline(WebGpuRenderPipeline.FromNative((nint)_gridPipeline));
//         renderPass.SetBindGroup(WebGpuBindGroup.FromNative((nint)_gridBindGroup));
//         renderPass.SetVertexBuffer(WebGpuBuffer.FromNative((nint)_gridVbo), (ulong)(6 * 3 * sizeof(float)));
//         renderPass.Draw(6);
//
//         // C. Draw the selection overlay after the grid.  The grid therefore
//         // uses the original scene depth, while the overlay can still mask
//         // itself with the selected object's depth.
//         if (selection != null)
//         {
//             MeshUniforms outlineUniforms = new MeshUniforms
//             {
//                 Model = selection.Model,
//                 View = view,
//                 Proj = proj,
//                 Color = GetMaterialColor(selection.Object),
//                 LightDir = GetMaterialLightDirection(selection.Object, lightDir),
//                 IsSelected = 0u
//             };
//             WebGpuApi.WriteBuffer(WebGpuQueue.FromNative((nint)_queue), selection.Resources.UniformBuffer,
//                 in outlineUniforms);
//
//             renderPass.SetPipeline(WebGpuRenderPipeline.FromNative((nint)_selectionDepthPipeline));
//             renderPass.SetBindGroup(selection.Resources.BindGroup);
//             if (MeshRenderPass.GetModel(selection.Object) != null && selection.Resources.ModelMeshes.Count > 0)
//             {
//                 MeshRenderPass.DrawModel(renderPass, selection.Resources, wireframe: false);
//             }
//             else
//             {
//                 DrawSelectionPrimitive(renderPass, selection.Object);
//             }
//
//             renderPass.SetPipeline(WebGpuRenderPipeline.FromNative((nint)_outlinePipeline));
//             renderPass.SetBindGroup(selection.Resources.BindGroup);
//             if (MeshRenderPass.GetModel(selection.Object) != null && selection.Resources.ModelMeshes.Count > 0)
//             {
//                 MeshRenderPass.DrawModel(renderPass, selection.Resources, wireframe: false);
//             }
//             else
//             {
//                 DrawSelectionPrimitive(renderPass, selection.Object);
//             }
//         }
//
//         renderPass.End();
//         commandList.Submit();
//         WebGpuApi.Wgpu.SurfacePresent(_surface);
//
//         WebGpuApi.Wgpu.TextureViewRelease(targetView);
//     }
//
//     private void DrawSelectionPrimitive(RenderPass pass, SceneObject obj)
//     {
//         if (obj.MeshType == "Pyramid")
//         {
//             pass.SetVertexBuffer(WebGpuBuffer.FromNative((nint)_pyramidVbo), (ulong)(16 * 6 * sizeof(float)));
//             pass.SetIndexBuffer(WebGpuBuffer.FromNative((nint)_pyramidEbo), WebGpuIndexFormat.Uint16,
//                 (ulong)(18 * sizeof(ushort)));
//             pass.DrawIndexed(18);
//         }
//         else
//         {
//             pass.SetVertexBuffer(WebGpuBuffer.FromNative((nint)_cubeVbo), (ulong)(24 * 6 * sizeof(float)));
//             pass.SetIndexBuffer(WebGpuBuffer.FromNative((nint)_cubeEbo), WebGpuIndexFormat.Uint16,
//                 (ulong)(36 * sizeof(ushort)));
//             pass.DrawIndexed(36);
//         }
//     }
//
//     private Vector4 GetMaterialColor(SceneObject obj)
//     {
//         if (obj.Material != null && ReferenceEquals(obj.Material.Shader, _meshShader) &&
//             obj.Material.TryGet("color", out Vector4 color))
//         {
//             return color;
//         }
//
//         return new Vector4(obj.ColorR, obj.ColorG, obj.ColorB, obj.ColorA);
//     }
//
//     private Vector3 GetMaterialLightDirection(SceneObject obj, Vector3 fallback)
//     {
//         if (obj.Material != null && ReferenceEquals(obj.Material.Shader, _meshShader) &&
//             obj.Material.TryGet("lightDir", out Vector3 lightDirection))
//         {
//             return lightDirection;
//         }
//
//         return fallback;
//     }
//
//     private void HandlePointerPressed(PointerPressedEventArgs e)
//     {
//         Focus();
//         var point = e.GetCurrentPoint(this);
//         if (point.Properties.IsRightButtonPressed || point.Properties.IsLeftButtonPressed)
//         {
//             _isOrbiting = true;
//             _lastMousePos = point.Position;
//             e.Pointer.Capture(this);
//             e.Handled = true;
//         }
//         else if (point.Properties.IsMiddleButtonPressed)
//         {
//             _isPanning = true;
//             _lastMousePos = point.Position;
//             e.Pointer.Capture(this);
//             e.Handled = true;
//         }
//     }
//
//     private void HandlePointerMoved(PointerEventArgs e)
//     {
//         if (_isOrbiting || _isPanning)
//         {
//             var point = e.GetCurrentPoint(this);
//             Point delta = point.Position - _lastMousePos;
//             _lastMousePos = point.Position;
//
//             if (Scene != null)
//             {
//                 if (_isOrbiting)
//                 {
//                     Scene.RotateCamera((float)delta.X * Scene.CameraRotationSensitivity, (float)delta.Y * Scene.CameraRotationSensitivity);
//                 }
//                 else if (_isPanning)
//                 {
//                     float sensitivity = Scene.CameraDistance * 0.002f;
//                     float yawRad = MathF.PI / 180f * Scene.CameraYaw;
//                     Vector3 right = new(MathF.Cos(yawRad), 0, -MathF.Sin(yawRad));
//                     Vector3 up = Vector3.UnitY;
//
//                     Vector3 panOffset = (right * (float)-delta.X + up * (float)delta.Y) * sensitivity;
//                     MoveCamera(panOffset);
//                 }
//             }
//
//             e.Handled = true;
//         }
//     }
//
//     private void HandlePointerReleased(PointerReleasedEventArgs e)
//     {
//         if (_isOrbiting || _isPanning)
//         {
//             _isOrbiting = false;
//             _isPanning = false;
//             e.Pointer.Capture(null);
//             e.Handled = true;
//         }
//     }
//
//     private void HandlePointerWheelChanged(PointerWheelEventArgs e)
//     {
//         if (Scene == null) return;
//         float yawRad = MathF.PI / 180f * Scene.CameraYaw;
//         float pitchRad = MathF.PI / 180f * Scene.CameraPitch;
//         MoveCamera(GetCameraForward(yawRad, pitchRad) * ((float)e.Delta.Y * 0.5f));
//         e.Handled = true;
//     }
//
//     private void UpdateKeyboardMovement(float deltaTime)
//     {
//         Scene? scene = _renderScene;
//         if (scene == null || deltaTime <= 0f) return;
//
//         // The WebGPU viewport is hosted in a native child HWND. Polling the
//         // keys while that HWND owns focus avoids depending on Avalonia's
//         // routed keyboard events crossing the native-control boundary.
//         if (_hwnd != nint.Zero && IsViewportWindowActive())
//         {
//             SetMovementKey(VK_Z, IsKeyDown(VK_Z));
//             SetMovementKey(VK_Q, IsKeyDown(VK_Q));
//             SetMovementKey(VK_S, IsKeyDown(VK_S));
//             SetMovementKey(VK_D, IsKeyDown(VK_D));
//         }
//         else
//         {
//             ClearMovementKeys();
//         }
//
//         float forwardInput = (_moveForward ? 1f : 0f) - (_moveBackward ? 1f : 0f);
//         float strafeInput = (_moveRight ? 1f : 0f) - (_moveLeft ? 1f : 0f);
//         if (forwardInput == 0f && strafeInput == 0f) return;
//
//         float yawRad = MathF.PI / 180f * scene.CameraYaw;
//         float pitchRad = MathF.PI / 180f * scene.CameraPitch;
//         Vector3 forward = GetCameraForward(yawRad, pitchRad);
//         Vector3 right = new(MathF.Cos(yawRad), 0f, -MathF.Sin(yawRad));
//         Vector3 direction = forward * forwardInput + right * strafeInput;
//
//         // Keep diagonal movement at the same speed as axial movement.
//         if (direction.LengthSquared() > 1f)
//         {
//             direction = Vector3.Normalize(direction);
//         }
//
//         Vector3 offset = direction * CameraMoveSpeed * deltaTime;
//         MoveCamera(offset);
//     }
//
//     private nint NativeWindowProc(nint hWnd, uint message, nint wParam, nint lParam)
//     {
//         switch (message)
//         {
//             case WM_KEYDOWN:
//                 SetMovementKey((int)wParam, true);
//                 if (IsMovementKey((int)wParam)) return nint.Zero;
//                 break;
//
//             case WM_KEYUP:
//                 SetMovementKey((int)wParam, false);
//                 if (IsMovementKey((int)wParam)) return nint.Zero;
//                 break;
//
//             case WM_KILLFOCUS:
//                 ClearMovementKeys();
//                 break;
//
//             case WM_LBUTTONDOWN:
//             case WM_RBUTTONDOWN:
//                 BeginNativeInteraction(orbit: true, GetMouseX(lParam), GetMouseY(lParam));
//                 return nint.Zero;
//
//             case WM_MBUTTONDOWN:
//                 BeginNativeInteraction(orbit: false, GetMouseX(lParam), GetMouseY(lParam));
//                 return nint.Zero;
//
//             case WM_MOUSEMOVE:
//                 if (_isOrbiting || _isPanning)
//                 {
//                     UpdateCamera(GetMouseX(lParam), GetMouseY(lParam));
//                     return nint.Zero;
//                 }
//                 break;
//
//             case WM_LBUTTONUP:
//             case WM_RBUTTONUP:
//             case WM_MBUTTONUP:
//                 if (_isOrbiting || _isPanning)
//                 {
//                     EndNativeInteraction();
//                     return nint.Zero;
//                 }
//                 break;
//
//             case WM_MOUSEWHEEL:
//                 ApplyZoom((short)((long)wParam >> 16));
//                 return nint.Zero;
//         }
//
//         return CallWindowProc(_previousWndProc, hWnd, message, wParam, lParam);
//     }
//
//     private void SetMovementKey(int key, bool isDown)
//     {
//         switch (key)
//         {
//             case VK_Z: _moveForward = isDown; break;
//             case VK_S: _moveBackward = isDown; break;
//             case VK_Q: _moveLeft = isDown; break;
//             case VK_D: _moveRight = isDown; break;
//         }
//     }
//
//     private static bool IsMovementKey(int key) =>
//         key is VK_Z or VK_S or VK_Q or VK_D;
//
//     private void ClearMovementKeys()
//     {
//         _moveForward = false;
//         _moveBackward = false;
//         _moveLeft = false;
//         _moveRight = false;
//     }
//
//     private void BeginNativeInteraction(bool orbit, int x, int y)
//     {
//         Focus();
//         SetFocus(_hwnd);
//         _isOrbiting = orbit;
//         _isPanning = !orbit;
//         _lastMousePos = new Point(x, y);
//         SetCapture(_hwnd);
//     }
//
//     private void UpdateCamera(int x, int y)
//     {
//         Point current = new(x, y);
//         Point delta = current - _lastMousePos;
//         _lastMousePos = current;
//
//         if (Scene == null) return;
//
//         if (_isOrbiting)
//         {
//             Scene.RotateCamera((float)delta.X * Scene.CameraRotationSensitivity, (float)delta.Y * Scene.CameraRotationSensitivity);
//         }
//         else if (_isPanning)
//         {
//             float sensitivity = Scene.CameraDistance * 0.002f;
//             float yawRad = MathF.PI / 180f * Scene.CameraYaw;
//             Vector3 right = new(MathF.Cos(yawRad), 0, -MathF.Sin(yawRad));
//             Vector3 panOffset = (right * (float)-delta.X + Vector3.UnitY * (float)delta.Y) * sensitivity;
//             MoveCamera(panOffset);
//         }
//     }
//
//     private void EndNativeInteraction()
//     {
//         _isOrbiting = false;
//         _isPanning = false;
//         ReleaseCapture();
//     }
//
//     private void ApplyZoom(float wheelDelta)
//     {
//         if (Scene != null)
//         {
//             float yawRad = MathF.PI / 180f * Scene.CameraYaw;
//             float pitchRad = MathF.PI / 180f * Scene.CameraPitch;
//             MoveCamera(GetCameraForward(yawRad, pitchRad) * (wheelDelta / 120f * 0.5f));
//         }
//     }
//
//     private static Vector3 GetCameraForward(float yawRad, float pitchRad) =>
//         Vector3.Normalize(new Vector3(
//             -MathF.Cos(pitchRad) * MathF.Sin(yawRad),
//             -MathF.Sin(pitchRad),
//             -MathF.Cos(pitchRad) * MathF.Cos(yawRad)));
//
//     private void MoveCamera(Vector3 offset)
//     {
//         (_renderScene ?? Scene)?.MoveCamera(offset);
//     }
//
//     private static int GetMouseX(nint lParam) => (short)((long)lParam & 0xFFFF);
//     private static int GetMouseY(nint lParam) => (short)(((long)lParam >> 16) & 0xFFFF);
//     private static bool IsKeyDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
//
//     private bool IsViewportWindowActive()
//     {
//         nint rootWindow = GetAncestor(_hwnd, GA_ROOT);
//         return rootWindow != nint.Zero && GetForegroundWindow() == rootWindow;
//     }
//
//     private void CleanupWebGpu()
//     {
//         if (_depthTextureView != null) WebGpuApi.Wgpu.TextureViewRelease(_depthTextureView);
//         if (_depthTexture != null) { WebGpuApi.Wgpu.TextureDestroy(_depthTexture); WebGpuApi.Wgpu.TextureRelease(_depthTexture); }
//
//         if (_meshPipeline != null) WebGpuApi.Wgpu.RenderPipelineRelease(_meshPipeline);
//         if (_pbrPipeline != null) WebGpuApi.Wgpu.RenderPipelineRelease(_pbrPipeline);
//         if (_transparentPbrPipeline != null) WebGpuApi.Wgpu.RenderPipelineRelease(_transparentPbrPipeline);
//         if (_wireframePipeline != null) WebGpuApi.Wgpu.RenderPipelineRelease(_wireframePipeline);
//         if (_selectionDepthPipeline != null) WebGpuApi.Wgpu.RenderPipelineRelease(_selectionDepthPipeline);
//         if (_outlinePipeline != null) WebGpuApi.Wgpu.RenderPipelineRelease(_outlinePipeline);
//         if (_gridPipeline != null) WebGpuApi.Wgpu.RenderPipelineRelease(_gridPipeline);
//
//         if (_meshShaderModule != null) WebGpuApi.Wgpu.ShaderModuleRelease(_meshShaderModule);
//         if (_pbrShaderModule != null) WebGpuApi.Wgpu.ShaderModuleRelease(_pbrShaderModule);
//         if (_gridShaderModule != null) WebGpuApi.Wgpu.ShaderModuleRelease(_gridShaderModule);
//
//         if (_gridBindGroup != null) WebGpuApi.Wgpu.BindGroupRelease(_gridBindGroup);
//
//         if (_meshBindGroupLayout != null) WebGpuApi.Wgpu.BindGroupLayoutRelease(_meshBindGroupLayout);
//         if (_pbrMaterialBindGroupLayout != null) WebGpuApi.Wgpu.BindGroupLayoutRelease(_pbrMaterialBindGroupLayout);
//         if (_gridBindGroupLayout != null) WebGpuApi.Wgpu.BindGroupLayoutRelease(_gridBindGroupLayout);
//
//         foreach (MeshGpuResources resources in _meshResources.Values)
//         {
//             ReleaseMeshResources(resources);
//         }
//         _meshResources.Clear();
//         if (_gridUniformBuffer != null) { WebGpuApi.Wgpu.BufferDestroy(_gridUniformBuffer); WebGpuApi.Wgpu.BufferRelease(_gridUniformBuffer); }
//
//         if (_cubeVbo != null) { WebGpuApi.Wgpu.BufferDestroy(_cubeVbo); WebGpuApi.Wgpu.BufferRelease(_cubeVbo); }
//         if (_cubeEbo != null) { WebGpuApi.Wgpu.BufferDestroy(_cubeEbo); WebGpuApi.Wgpu.BufferRelease(_cubeEbo); }
//         if (_cubeWireframeEbo != null) { WebGpuApi.Wgpu.BufferDestroy(_cubeWireframeEbo); WebGpuApi.Wgpu.BufferRelease(_cubeWireframeEbo); }
//         if (_pyramidVbo != null) { WebGpuApi.Wgpu.BufferDestroy(_pyramidVbo); WebGpuApi.Wgpu.BufferRelease(_pyramidVbo); }
//         if (_pyramidEbo != null) { WebGpuApi.Wgpu.BufferDestroy(_pyramidEbo); WebGpuApi.Wgpu.BufferRelease(_pyramidEbo); }
//         if (_pyramidWireframeEbo != null) { WebGpuApi.Wgpu.BufferDestroy(_pyramidWireframeEbo); WebGpuApi.Wgpu.BufferRelease(_pyramidWireframeEbo); }
//         if (_gridVbo != null) { WebGpuApi.Wgpu.BufferDestroy(_gridVbo); WebGpuApi.Wgpu.BufferRelease(_gridVbo); }
//
//         if (_surface != null) WebGpuApi.Wgpu.SurfaceRelease(_surface);
//
//         _deviceWrapper?.Dispose();
//         _adapterWrapper?.Dispose();
//         WebGpuApi.Dispose();
//     }
//
//     [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
//     private static extern nint CreateWindowExW(
//         int dwExStyle, string lpClassName, string lpWindowName,
//         int dwStyle, int x, int y, int nWidth, int nHeight,
//         nint hWndParent, nint hMenu, nint hInstance, nint lpParam);
//
//     [DllImport("user32.dll", SetLastError = true)]
//     private static extern bool DestroyWindow(nint hWnd);
//
//     [DllImport("user32.dll", SetLastError = true)]
//     private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
//
//     private delegate nint NativeWndProc(nint hWnd, uint message, nint wParam, nint lParam);
//
//     private const int GWLP_WNDPROC = -4;
//     private const uint WM_LBUTTONDOWN = 0x0201;
//     private const uint WM_LBUTTONUP = 0x0202;
//     private const uint WM_RBUTTONDOWN = 0x0204;
//     private const uint WM_RBUTTONUP = 0x0205;
//     private const uint WM_MBUTTONDOWN = 0x0207;
//     private const uint WM_MBUTTONUP = 0x0208;
//     private const uint WM_MOUSEMOVE = 0x0200;
//     private const uint WM_MOUSEWHEEL = 0x020A;
//     private const uint WM_KEYDOWN = 0x0100;
//     private const uint WM_KEYUP = 0x0101;
//     private const uint WM_KILLFOCUS = 0x0008;
//     private const int VK_Z = 0x5A;
//     private const int VK_Q = 0x51;
//     private const int VK_S = 0x53;
//     private const int VK_D = 0x44;
//
//     [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
//     private static extern nint SetWindowLongPtr(nint hWnd, int index, nint newValue);
//
//     [DllImport("user32.dll", SetLastError = true)]
//     private static extern nint CallWindowProc(nint previousWndProc, nint hWnd, uint message, nint wParam, nint lParam);
//
//     [DllImport("user32.dll")]
//     private static extern nint SetCapture(nint hWnd);
//
//     [DllImport("user32.dll")]
//     private static extern nint SetFocus(nint hWnd);
//
//     [DllImport("user32.dll")]
//     private static extern short GetAsyncKeyState(int vKey);
//
//     [DllImport("user32.dll")]
//     private static extern nint GetForegroundWindow();
//
//     [DllImport("user32.dll")]
//     private static extern nint GetAncestor(nint hWnd, uint gaFlags);
//
//     private const uint GA_ROOT = 2;
//
//     [DllImport("user32.dll")]
//     private static extern bool ReleaseCapture();
// }