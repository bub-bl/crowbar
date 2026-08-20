# Crowbar

Base runtime rebuilt around Silk.NET:

- `Crowbar.Engine` exposes the platform contracts and the SDL implementation.
- `Crowbar.Editor` is currently the application bootstrap and creates the window via Silk.NET.Windowing.
- WebGPU rendering and the UI will be added in separate layers.

To launch the window:

```powershell
dotnet run --project src\Editor\Editor.csproj
```