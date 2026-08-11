# Vision de Crowbar

## Objectif

Crowbar n'est pas un moteur de jeu généraliste mais une **plateforme
Sandbox**. Le runtime est le jeu, les expériences sont des addons et
l'éditeur est intégré au runtime.

> **Principe directeur :** chaque sous-système (UI, rendu, audio,
> physique, scripting, etc.) est conçu derrière une abstraction claire
> afin de pouvoir évoluer ou être remplacé sans impacter le reste du
> runtime.

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
│   └── VulkanGraphicsDevice (futur)
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

## Une seule application

Le joueur peut passer instantanément entre : - Jouer - Créer -
Développer

Aucun redémarrage ni recompilation.

## Philosophie

-   Simplicité avant complexité.
-   Les outils sont aussi importants que le moteur.
-   Les expériences et les outils sont des plugins.
-   Les abstractions priment sur les implémentations.

# Platform

Créer une abstraction `IPlatform`.

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

Implémentation initiale :

``` text
IPlatform
    ↓
SDL3Platform
    ↓
Silk.NET SDL bindings
    ↓
SDL3
```

**Ne pas utiliser Silk.NET Windowing ou Silk.NET Input.**

Utiliser uniquement les bindings SDL3 fournis par Silk.NET.

# UI

-   Pas d'Avalonia.
-   Framework UI maison.
-   Implémentation Razor custom inspirée de s&box.
-   Pas de Blazor, DOM ou navigateur.

Pipeline :

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

Le Canvas est abstrait. Skia est uniquement le premier backend.

# Graphics

Le moteur ne dépend jamais directement d'une API graphique.

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

Backend initial : **WebGPU**.

Backend futur : **Vulkan**, sans modifier le reste du moteur.

Le runtime manipule uniquement :

-   GraphicsDevice
-   Texture
-   Buffer
-   Pipeline
-   CommandBuffer
-   Shader
-   Sampler

Jamais des types spécifiques à WebGPU ou Vulkan.

**État actuel :** les abstractions existent dans `Crowbar.Engine.Rendering`
(`IGraphicsDevice`, `ITexture`, `IBuffer`, `IPipeline`, `IBindGroup`,
`ICommandBuffer`, `IRenderPass`, `ISwapchain`). Le moteur de rendu du runtime
(`Renderer`) ne manipule que ces types ; `WebGpuContext` est un backend concret
qui les implémente. Un backend Vulkan, un renderer de test ou headless peuvent
être ajoutés sans toucher au runtime.

# Priorités

1.  Runtime
2.  Plateforme Sandbox
3.  Plugin System
4.  Asset Pipeline
5.  UI Framework
6.  Razor
7.  Hot Reload
8.  Networking
9.  Outils
10. Expérience développeur

Le rendu est important, mais il ne doit jamais dicter l'architecture
globale.
