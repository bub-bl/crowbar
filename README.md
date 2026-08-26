# Crowbar

Base runtime rebuilt around Silk.NET:

- `Crowbar.Engine` exposes the platform contracts and the SDL implementation.
- `Crowbar.Editor` is currently the application bootstrap and creates the window via Silk.NET.Windowing.
- WebGPU rendering and the UI will be added in separate layers.

To launch the window:

```powershell
dotnet run --project src\Editor\Editor.csproj
```

Tests are split by subsystem into separate xUnit projects. Run all tests:

```powershell
dotnet test Crowbar.slnx
```

Run one test group:

```powershell
dotnet test tests\Crowbar.Audio.Tests\Crowbar.Audio.Tests.csproj
dotnet test tests\Crowbar.Engine.Tests\Crowbar.Engine.Tests.csproj
dotnet test tests\Crowbar.FileSystem.Tests\Crowbar.FileSystem.Tests.csproj
dotnet test tests\Crowbar.UI.Tests\Crowbar.UI.Tests.csproj
dotnet test tests\Crowbar.Editor.Tests\Crowbar.Editor.Tests.csproj
```

Tests run headless and do not require a GPU.

## Units

**One world unit equals one meter.** Crowbar follows the Unity/Godot convention:
world coordinates, transforms, the editor grid, camera distances and light
ranges are all in meters, with Y-up / Z-forward and a left-handed projection.

3D model imports are normalized to meters. glTF is always authored in meters and
imports at 1:1 by default; other formats (OBJ, FBX, …) carry no unit metadata,
so the model's `ImportScale` (`Model.Load(path, importScale)` /
`ResourceLibrary.LoadModel(path, importScale)`) converts the source unit:
`0.01` for centimeters, `0.0254` for inches. The scale is baked into the
geometry at import time.

## Project layout

- `src/Engine` - engine runtime (rendering, rendering2D, world, audio, input, scripting, project/file formats).
- `src/Editor` - application bootstrap and editor UI (Razor components).
- `src/UI` - UI framework (layout, styling, Razor pipeline).
- `src/FileSystem` - file system abstraction over Zio.
- `tests/Crowbar.TestSupport` - shared test fixtures and UI helpers.
- `tests/Crowbar.Audio.Tests` - audio tests.
- `tests/Crowbar.Engine.Tests` - engine, rendering, world, shader, and scripting tests.
- `tests/Crowbar.FileSystem.Tests` - file system tests.
- `tests/Crowbar.UI.Tests` - UI framework tests.
- `tests/Crowbar.Editor.Tests` - editor integration tests.
- `Game/` - demo game project.
- `Assets/` - game assets (icons, models).
- `Shaders/`, `src/Engine/Shaders` - GPU shaders (Slang/WGSL).
- `docs/` - file format documentation.