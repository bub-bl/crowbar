# Prompt: Physically-based sky atmosphere model (Bruneton/Nishita) with quality-of-life improvements

> Feature suggestion: *Sky atmosphere model (e.g., Bruneton/Nishita) Quality-of-Life Improvements*.
> Target: the procedural sky, internally named `ProceduralAtmosphere` (component `ProceduralSkyComponent`), informally "PhysicalSky".

## Goal

Replace the current fake procedural sky (a hardcoded horizon→zenith gradient plus a cosmetic sun disc) with a physically-based atmospheric scattering model — Bruneton-style precomputed LUTs or Nishita analytic — and add the quality-of-life improvements that make it usable: sun direction driven by the scene's directional light, tunable parameters in the editor, serialization in `.level` files, and IBL consistency so PBR reflections match the sky.

## Current state (verify before starting)

- `src/Engine/Environment/SkyProvider.cs` — `ProceduralAtmosphere : SkyProvider` is a marker type with no parameters.
- `Shaders/Common/Environment.slang` — `EnvironmentLighting::SampleSky` is the entire procedural sky: a `lerp(horizon, zenith, pow(altitude, 0.65))` gradient plus a fake sun disc `pow(max(dot(rotated, sunDirection), 0.0), 128.0)` with a **hardcoded** sun direction `float3(-0.35, 0.8, -0.2)` and hardcoded colors. `EnvironmentLighting::EvaluateIbl` samples precomputed cubemaps.
- `Shaders/Environment/Sky.slang` — fullscreen pass (`vs_main`/`fs_main`) that calls `EnvironmentLighting::SampleSky(worldDirection)`.
- `src/Engine/Rendering/Renderer.cs` — `UpdateEnvironment` fills `EnvironmentUniforms` (`parameters` = rotation, intensity, exposure, max specular mip; `tint`; `provider` flag). No sun direction or atmosphere parameters are passed today.
- `src/Engine/World/SkyComponents.cs` + `EnvironmentComponent.cs` — `ProceduralSkyComponent` inherits only `Rotation`, `Intensity`, `Exposure`, `Tint` (`[Property]` attributes surfaced automatically in the inspector).
- `src/Engine/World/DirectionalLight.cs` — `DirectionalLight.Direction => World.Rotation.Forward`; this is the natural source for the sky's sun direction.
- `src/Engine/World/LevelSerializer.cs` — `RestoreEnvironment` builds `new ProceduralAtmosphere()` with no extra data; component properties round-trip through `RestoreComponentProperties`.
- `src/Engine/Environment/EnvironmentPreprocessor.cs` — procedural skies skip cubemap preprocessing; `Renderer.GetEnvironmentBindGroup` falls back to 1×1 default cubemaps, so IBL does not match the sky.
- `docs/LevelFileFormat.md` — documents the environment serialization format (update it).
- Tests live under `tests/Crowbar.Engine.Tests/Environment/EnvironmentTests.cs`.

## Requirements

1. **Atmosphere model.** Implement a real-time physical model in Slang (`#language slang 2026`, matching the existing shaders): Bruneton precomputed scattering (transmittance + single/multiple scattering LUTs) or a Nishita-style analytic model. Must be a few lookups per pixel. GPU-only: any LUT generation must happen in compute passes on the GPU, following the pattern `EnvironmentPreprocessor` uses for `BrdfLut` (compute pipeline created from a `Shaders/Environment/*.slang` shader, dispatched once, cached). No CPU precompute.
2. **Sun direction.** Drive the sky's sun from the scene's directional light: pass the first `DirectionalLight`'s world-space direction into the shader through `EnvironmentUniforms` (extend the struct). Make the sun disc's angular radius configurable. Keep `Rotation` semantics working (applied on top of the sun direction).
3. **Tunable parameters.** Expose the model's parameters as `[Property]` on `ProceduralSkyComponent` with sensible defaults that preserve the current look: e.g. turbidity, ground/earth albedo, sun intensity, sun angular radius, ozone, exposure. Edits must apply live, without reload, through the existing `SceneEnvironment` → `UpdateEnvironment` uniform path.
4. **Serialization.** Persist the new parameters in `.level` files via the existing component-properties mechanism; old files must still load with defaults; update `docs/LevelFileFormat.md`.
5. **IBL consistency.** Reflections/irradiance for procedural skies must come from the same atmosphere model, so metallic surfaces reflect the sky instead of the default 1×1 texture. Either generate the environment/irradiance/prefiltered cubemaps from the sky on the GPU, or evaluate the analytic model in `EvaluateIbl` for the procedural path.
6. **Quality-of-life.**
   - Keep the existing `Rotation`/`Intensity`/`Exposure`/`Tint` environment settings working as they do today.
   - Editor presets (e.g. Midday, Sunset, Night) are a nice-to-have, not required.
7. **Tests & validation.** Add or extend xUnit tests (e.g. `tests/Crowbar.Engine.Tests/Environment/EnvironmentTests.cs`) covering: serialization round-trip of the new parameters, provider restoration, and defaults when loading files that predate the parameters. `dotnet test Crowbar.slnx` must pass and the solution must build (`dotnet build Crowbar.slnx`).
8. **Constraints.** All code, comments, docs, and UI strings in English (repository policy). Follow the surrounding code style. No new external dependencies unless already used by the project.

## Out of scope

- Volumetric clouds, weather systems, rainbows, stars, moon, night-side lights.
- Changing the cubemap environment path.

## Acceptance criteria

- The sky visibly behaves like a real atmosphere: blue zenith, horizon glow, sun-reddening at low elevation, and a sun disc aligned with the directional light that casts shadows.
- All new parameters are editable in the inspector, apply live, and survive save/load; legacy files load with defaults.
- PBR reflections/irradiance match the sky for procedural environments.
- All tests pass and the solution builds.

## Suggested approach

1. Shader first: replace the gradient branch in `SampleSky` with the atmosphere model, keeping `Rotate`/`ApplySettings`. Iterate visually in the editor.
2. Thread sun direction and atmosphere parameters through `EnvironmentUniforms` ← `UpdateEnvironment` ← `SceneEnvironment` (add backing fields and `Restore` support).
3. Add `[Property]`s to `ProceduralSkyComponent`; extend `LevelSerializer` defaults for old files.
4. Implement IBL for the procedural path (LUT-based or analytic).
5. Tests + `docs/LevelFileFormat.md` update.

## Definition of done

- Shader implements a physically-based model; no hardcoded sun direction or gradient remains for the procedural path.
- Sun follows the directional light; parameters live-edit and persist.
- IBL matches the sky.
- `dotnet test Crowbar.slnx` green; build green; `docs/LevelFileFormat.md` updated.
