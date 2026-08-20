# Crowbar Vision

## Objective

Crowbar is not a general-purpose game engine but a **Sandbox
platform**. The runtime is the game, experiences are addons, and the
editor is integrated into the runtime.

> **Guiding principle:** every subsystem (UI, rendering, audio,
> physics, scripting, etc.) is designed behind a clear abstraction so it
> can evolve or be replaced without affecting the rest of the runtime.

------------------------------------------------------------------------

# Architecture

``` text
Crowbar
├── Platform
│   ├── SDL3 (via Silk.NET SDL bindings)
│   ├── Windowing
│   ├── Input
│   ├── Clipboard
│   ├── Drag & Drop
│   ├── Gamepads
│   └── File Dialogs
│
├── Graphics
│   ├── IGraphicsDevice
│   ├── WebGpuGraphicsDevice
│   └── VulkanGraphicsDevice (future)
│
├── Runtime
│   ├── Renderer
│   ├── Physics
│   ├── Audio
│   ├── Networking
│   ├── Asset System
│   ├── Plugin System
│   ├── UI Framework
│   └── Scripting
│
├── Editor
└── Experiences
```

## A single application

The player can switch instantly between: - Playing - Creating -
Developing

No restart or recompilation.

## Philosophy

-   Simplicity over complexity.
-   Tools are as important as the engine.
-   Experiences and tools are plugins.
-   Abstractions come before implementations.

# Platform

Create an `IPlatform` abstraction.

``` csharp
public interface IPlatform
{
    IWindow CreateWindow(WindowOptions options);

    IClipboard Clipboard { get; }
    ICursor Cursor { get; }
    IGamepadManager Gamepads { get; }
    IFileDialog FileDialog { get; }
}
```

Initial implementation:

``` text
IPlatform
    ↓
SDL3Platform
    ↓
Silk.NET SDL bindings
    ↓
SDL3
```

**Do not use Silk.NET Windowing or Silk.NET Input.**

Use only the SDL3 bindings provided by Silk.NET.

# UI

-   No Avalonia.
-   In-house UI framework.
-   Custom Razor implementation inspired by s&box.
-   No Blazor, DOM, or browser.

Pipeline:

``` text
Razor
↓
Widget Tree
↓
Layout
↓
Animations
↓
Paint Commands
↓
Canvas API
↓
Skia Backend
↓
Renderer
```

The Canvas is abstract. Skia is only the first backend.

# Graphics

The engine never depends directly on a graphics API.

``` text
Runtime
↓
IGraphicsDevice
↓
WebGpuGraphicsDevice
↓
Silk.NET WebGPU
↓
WebGPU
```

Initial backend: **WebGPU**.

Future backend: **Vulkan**, without modifying the rest of the engine.

The runtime only manipulates:

-   GraphicsDevice
-   Texture
-   Buffer
-   Pipeline
-   CommandBuffer
-   Shader
-   Sampler

Never types specific to WebGPU or Vulkan.

**Current state:** the abstractions exist in `Crowbar.Engine.Rendering`
(`IGraphicsDevice`, `ITexture`, `IBuffer`, `IPipeline`, `IBindGroup`,
`ICommandBuffer`, `IRenderPass`, `ISwapchain`). The runtime renderer
(`Renderer`) only manipulates these types; `WebGpuContext` is a concrete
backend that implements them. A Vulkan backend, a test renderer, or a
headless one can be added without touching the runtime.

# Priorities

1.  Runtime
2.  Sandbox Platform
3.  Plugin System
4.  Asset Pipeline
5.  UI Framework
6.  Razor
7.  Hot Reload
8.  Networking
9.  Tools
10. Developer experience

Rendering is important, but it must never dictate the overall
architecture.