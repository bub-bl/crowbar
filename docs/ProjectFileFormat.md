# The `.crproj` file format

A Crowbar project is persisted in a versioned JSON file that is readable by a
human and diff-friendly in version control. The container is the DTO
`CrowbarProjectData` (`src/Engine/Project/CrowbarProjectData.cs`), written by
`CrowbarProjectSerializer.Serialize` and read back by
`CrowbarProjectSerializer.Deserialize`. The asset is manipulated by
`CrowbarProjectFile` (`src/Engine/Project/CrowbarProjectFile.cs`), which saves
atomically: writing to a `.tmp` file and then moving over the destination.

The file lives **at the root of the project directory it describes**: that
directory <em>is</em> the project. The project is laid out with two folders
next to the `.crproj`: `Content/` for the game's assets and `.level` levels,
and `Code/` for the game project's C# sources (`Game.csproj`).

## Example

```json
{
  "format": 1,
  "id": "8f4d2a1e-9c2b-4e5d-a1b2-3c4d5e6f7081",
  "name": "MyGame",
  "version": "0.1",
  "author": "Jane Doe",
  "description": "A Crowbar demo game."
}
```

## Fields

| Field | Type | Role |
|---|---|---|
| `format` | int | Format version. `CrowbarProjectFile.CurrentFormat` (1 today). A file newer than the build **refuses** to load; an older file loads with a warning (migration point). |
| `id` | string (GUID) | Stable identity of the project, preserved across save/load (external references: launcher, build scripts). |
| `name` | string | Display name of the project (shown in the editor title bar). |
| `version` | string | Version of the project itself (product version, not the editor's). |
| `author` | string \| null | Project author, or absent. |
| `description` | string \| null | Short project description, or absent. |

## Loading

- **Double-click / command line**: the editor receives the `.crproj` path as
  its first argument (`Crowbar.Editor.exe "C:\Projects\MyGame\MyGame.crproj"`).
  It reads it via `CrowbarProjectFile.LoadFromDisk` (before any filesystem
  configuration) and then **roots the project filesystem on the file's
  directory**, so levels are saved in the project's `Content/` folder and the
  game project code in `Code/`, both under the project directory.
- **From the editor**: the "folder" button in the toolbar opens the Windows
  Explorer window (native `GetOpenFileName` dialog) to pick a `.crproj`; the
  editor then re-roots the project filesystem, reloads the level, and
  recompiles the new project's game project.
- **Bare start** (no argument): the editor falls back to the demo project
  (`Game/` directory), without a project file.

## Compatibility contract

Like the `.level` format, a `format` **newer** than the build is a hard failure
(`InvalidDataException`): a clear message is preferred to silent corruption.
An older format loads with a warning.

## Notes

- Writing is atomic (`.tmp` → move), so a crash never leaves a truncated file.
- The `.crproj` → editor association on Windows is registered with
  `tools\RegisterCrowbarProject.ps1` (HKCU registry, no admin rights).