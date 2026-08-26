# Units

Crowbar uses **one world unit = one meter**, following the Unity/Godot
convention. Every spatial value in the engine — world positions, transforms,
the editor grid and gizmo snap, camera orbit distance and clip planes, light
ranges — is expressed in meters.

- Coordinate basis: **Y-up, Z-forward**, left-handed projection (Unity/DirectX).
- Angles are in radians in engine math; editor-facing values (e.g. camera FOV)
  are converted to degrees.
- Physical constants that will rely on units (e.g. a future `9.81 m/s²`
  gravity) are meter-consistent.

## Imported models

Files that declare their units are respected automatically:

- **glTF** is `meter` by specification; it imports at 1:1 with the default
  import scale.
- **OBJ, FBX, STL, DAE, PLY, …** carry no unit metadata. Their authoring unit is
  converted to meters through the model's `ImportScale`:

| Source unit | `ImportScale` |
|---|---|
| meters | `1.0` (default) |
| centimeters | `0.01` |
| inches | `0.0254` |

The scale is supplied at load time and baked into the geometry and node
transforms — it cannot be changed after import. Provide it through
`Model.Load(path, importScale)` or `ResourceLibrary.LoadModel(path, importScale)`.

```csharp
// A Sketchfab model authored in centimeters.
var model = ResourceLibrary.LoadModel("Content/Models/work_light.gltf", importScale: 0.01f);
```

When a cached model was imported with a different scale than requested, the
cache entry is refreshed so the returned instance carries the requested scale.