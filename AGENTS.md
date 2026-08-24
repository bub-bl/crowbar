# AGENTS.md

Guidelines for AI agents (and humans) working in the Crowbar repository.

## Language policy (English only)

The entire project must be written in English. This applies to everything, without exception:

- Documentation (`README.md`, `docs/`, design docs, any `.md` file)
- Source code
- Code comments and XML doc comments
- Tests (names, assertions, and test data)
- User-facing strings (UI labels, tooltips, console/log output, notifications)
- Commit messages and PR descriptions
- File names and directory names

Rules:

- Never write code, comments, docs, or tests in any language other than English.
- Never mix French (or any other language) into existing files.
- Never invent pseudo-English hybrids; use clear, standard English.
- Proper nouns and technical terms are fine (e.g. Bézier, de Casteljau, WebGPU, SDL, Silk.NET, MSDF).
- When a file is touched, it must remain 100% English afterwards.

## Build

The solution is `Crowbar.slnx`. Run the editor:

```powershell
dotnet run --project src\Editor\Editor.csproj
```

## Tests

Tests are split into subsystem-specific xUnit projects. Run all tests:

```powershell
dotnet test Crowbar.slnx
```

All tests must pass before a change is considered complete. Tests run headless and do not require a GPU.

## Project layout

- `src/Engine` — engine runtime (rendering, rendering2D, world, audio, input, scripting, project/file formats).
- `src/Editor` — application bootstrap and editor UI (Razor components).
- `src/UI` — UI framework (layout, styling, Razor pipeline).
- `src/FileSystem` — file system abstraction over Zio.
- `tests/Crowbar.TestSupport` — shared test fixtures and helpers.
- `tests/Crowbar.Audio.Tests` — audio tests.
- `tests/Crowbar.Engine.Tests` — engine tests.
- `tests/Crowbar.FileSystem.Tests` — file system tests.
- `tests/Crowbar.UI.Tests` — UI framework tests.
- `tests/Crowbar.Editor.Tests` — editor integration tests.
- `Game/` — demo game project.
- `Assets/` — game assets (icons, models).
- `Shaders/`, `src/Engine/Shaders` — GPU shaders (Slang/WGSL).
- `docs/` — file format documentation.

## Conventions

- Follow the existing code style in the surrounding files.
- Keep all strings, identifiers, and comments in English.