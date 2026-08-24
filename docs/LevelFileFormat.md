# The `.level` file format

A level (`Level`) is persisted in a versioned JSON file that is readable by a
human and diff-friendly in version control. The container is the DTO
`LevelFileData` (`src/Engine/World/LevelFileData.cs`), written by
`LevelSerializer.Serialize` and read back by `LevelSerializer.Deserialize` +
`CreateLevel`. The asset is manipulated by `LevelFile` (`src/Engine/World/LevelFile.cs`),
which saves into the project filesystem (`FileSystem.Project`) atomically:
writing to a `.tmp` file and then `MoveFile` over the destination.

## Example

```json
{
  "format": 1,
  "id": "8f4d2a1e-...",
  "metadata": { "name": "Demo", "version": "0.1" },
  "entities": [
    {
      "id": "c1b0...",
      "name": "Cube",
      "components": [
        {
          "type": "MeshRenderer",
          "transform": "0,0.5,0,0.259,0.122,0.951,0.065,1,1,1",
          "properties": {
            "Model": { "procedural": "cube" },
            "Material": {
              "shader": "Surface/StandardPbr",
              "technique": "Main",
              "values": {
                "color": [0.2, 0.6, 1.0, 1.0],
                "metallic": 0.15,
                "roughness": 0.45,
                "occlusion": 1,
                "emissive": 0
              },
              "blendMode": "Opaque",
              "doubleSided": false
            }
          }
        }
      ]
    }
  ],
  "attachments": [
    { "parent": "c1b0...", "child": "d2c1..." }
  ]
}
```

## Fields

| Field | Type | Role |
|---|---|---|
| `format` | int | Format version. `LevelFile.CurrentFormat` (1 today). A file newer than the build **refuses** to load; an older file loads with a warning (migration point). |
| `id` | string (GUID) | Stable identity of the level, preserved across save/load. |
| `metadata` | object | `name` (display name of the level) and `version` (version of the editor that wrote the file). |
| `entities` | array | The level's entities, in spawn order. |
| `attachments` | array | Parent → child relationships between transforms, resolved **in a second phase** after all entities are created (file order is unconstrained). |

### Entity

| Field | Role |
|---|---|
| `id` | Stable GUID of the entity, restored on load (never regenerated: external references survive). |
| `name` | Display name. |
| `components` | The attached components. |

### Component

| Field | Role |
|---|---|
| `type` | Short name of the type (e.g. `MeshRenderer`, `PointLight`). Resolved via `TypeRegistry` on load. |
| `transform` | Local transform of spatial components (`TransformComponent`), in canonical form `px,py,pz,rx,ry,rz,rw,sx,sy,sz` (invariant culture). Absent for purely logical components. |
| `properties` | Values of public **writable** properties marked `[Property]` (the same reflective contract as the inspector). Read-only properties (derived values, e.g. `DirectionalLight.Direction`) are not persisted: they recompute. |

### Property values

Values are written in native JSON form, invariant culture:

| CLR type | JSON form |
|---|---|
| `float`, `double`, `int`, `uint`, `bool`, `string` | primitive |
| `Vector2` / `Vector3` / `Vector4` | array of numbers |
| `Transform` | canonical string |
| `Material` | object `{ shader, technique, values, blendMode, doubleSided }` — `values` is a parameter name → value object (validated against the shader reflection on load) |
| `Model` | object `{ "path": "..." }` for a file model, or `{ "procedural": "cube" \| "plane" }` for a primitive |
| `enum` | member name |

### Procedural sky parameters

A `ProceduralSkyComponent` (the procedural atmosphere provider) persists the
atmosphere parameters as regular `[Property]` values, alongside the inherited
`Rotation` / `Intensity` / `Exposure` / `Tint`:

| Property | Type | Role |
|---|---|---|
| `Turbidity` | float | Haze multiplier for Mie scattering (0.1..10, default 1 = clear air). |
| `GroundAlbedo` | float | Diffuse ground reflectance (0..1, default 0.3), feeding the sky's ground bounce. |
| `SunAngularRadius` | float | Angular radius of the sun disc in degrees (0.1..10, default 1.5). |
| `SunIntensity` | float | Multiplier on the sun's radiance (default 1). |

The sun *direction* is not persisted: it follows the scene's first enabled
`DirectionalLight` each frame (with a fixed fallback when the level has none),
like the read-only `DirectionalLight.Direction`. Files written before these
parameters existed load them with their defaults, so old levels are unchanged.

### Post-process component

A `PostProcessComponent` (attachable to any entity) configures the frame's
post-process — currently the tonemapper applied to the whole rendered scene
(sky included). The first enabled instance in the world wins; without one the
engine keeps the historical Reinhard look.

| Property | Type | Role |
|---|---|---|
| `Operator` | enum | Tone curve: `None`, `Reinhard`, `Aces` (default), `Agx`. |
| `Exposure` | float | Exposure in stops (2^exposure), applied before the curve (default 0). |
| `Saturation` | float | Post-tonemap saturation multiplier (default 1). |

## Compatibility contract (forward compatibility)

Reading is tolerant — a level never fails to load because of unknown content:

- an unknown component `type` (renamed, removed) is **ignored with a warning**;
- an unknown, read-only, or non-restorable property is ignored with a warning;
- a material whose parameter no longer exists in the shader is ignored;
- a model that cannot be found is ignored (the property stays `null`).

On the other hand, a `format` **newer** than the build is a hard failure
(`InvalidDataException`): a clear message is preferred to silent corruption.

## Notes

- Out-of-level entities (world-only, e.g. the editor camera) are never serialized.
- A duplicate component in an entity is ignored with a warning (an entity allows only one component per type).
- A material's textures are not persisted in v1 (they come from the model import).
- Writing is atomic (`.tmp` write → `MoveFile`), so a crash never leaves a truncated file.