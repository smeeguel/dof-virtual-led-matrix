# Virtual DOF Matrix - Agent Brief

## Project Goal

Build and maintain a standalone, redistributable virtual LED matrix viewer for the Visual Pinball / DOF ecosystem.

The app should:

- run on Windows for VPX/DOF cabinets
- work without LED hardware
- integrate with DOF through this repo's virtual DirectOutput controller
- display one or more virtual LED toys in borderless, movable, resizable, always-on-top WPF windows
- keep rendering overhead low enough for cabinet use
- persist runtime, window, routing, and visual settings in `settings.json`
- package cleanly for community sharing as a ZIP and a standalone installer EXE

## Current Integration Model

The current supported transport is named pipe output from a bundled/custom `DirectOutput.dll`.

Runtime path:

```text
DOF VirtualLEDStripController -> named pipe VDMF frames -> VirtualDofMatrix.App -> routed toy windows/adapters
```

Key points:

- DOF must load `VirtualLEDStripController` from this repo's `DirectOutput.dll` payload.
- The app acts as the named-pipe server and should be running before DOF initializes the table.
- The default frame pipe is `VirtualDofMatrix`.
- The default control pipe is `VirtualDofMatrix.Control`.
- Do not design new runtime work around removed prototype transports unless the user explicitly asks for archival/prototype investigation.

## Current Architecture

Primary projects:

- `src/VirtualDofMatrix.App`
  - WPF executable.
  - Owns app startup, config persistence, transport host, control pipe, settings UI, toy window orchestration, and renderer selection.
- `src/VirtualDofMatrix.Core`
  - UI-agnostic shared models and transformation logic.
  - Owns frame structures, matrix mapping, toy routing, config contracts, and compatibility defaults.
- `src/VirtualDofMatrix.Tests`
  - Unit tests for parser, mapping, routing, config, rendering boundaries, and cabinet XML behavior.
- `installer/VirtualDofMatrix.Installer`
  - Standalone .NET/WPF installer executable.
  - Embeds the app payload and release payload; current packaging targets this standalone installer EXE.

High-level dependency direction:

```text
VirtualDofMatrix.App -> VirtualDofMatrix.Core
VirtualDofMatrix.Tests -> App/Core as needed
```

Keep parsing, routing, and rendering separable. Core should stay UI-agnostic.

## Runtime Flow

1. DOF loads the virtual controller from the bundled `DirectOutput.dll`.
2. `VirtualLEDStripController` writes binary `VDMF` messages to the configured named pipe.
3. `FrameTransportHost` accepts the pipe connection.
4. `FrameTransportStreamParser` reconstructs complete messages from the byte stream.
5. `ToyRouter` maps the incoming payload into configured virtual toys.
6. Output adapters deliver each toy frame to viewer windows and optional external consumers.
7. WPF renderers present the routed frames using the selected GPU or CPU path.

The parser must remain stream-safe: it should handle split reads, combined reads, malformed headers, reconnects, and payload guardrails.

## VDMF Message Envelope

The DirectOutput virtual controller emits:

- magic: ASCII `VDMF`
- message type:
  - `1` = RGB payload frame
  - `2` = table/ROM metadata
- sequence: little-endian `Int32`
- payload length: little-endian `Int32`
- payload bytes

The app enforces the current DOF-compatible payload guardrail of `8 * 1100 * 3` RGB bytes.

## Configuration

Main config file: `settings.json`.

Sample config: `examples/settings.sample.json`.

Important config areas:

- `transport`: named pipe and control pipe names.
- `matrix`: dimensions, mapping, renderer backend, dot shape, tone mapping, smoothing, bloom, and visual quality settings.
- `window`: position, size, borderless, topmost, and aspect behavior.
- `debug`: protocol and frame logging.
- `routing`: toy definitions, source ranges, policies, and output targets.
- `settings`: startup behavior, cabinet XML path hints, and table/front-end automation options.

If settings are added, removed, or renamed, update `examples/settings.sample.json`.

## Matrix And Routing Model

- Router output is canonical linear RGB order.
- Visual mapping transforms such as serpentine layout, rotation, or display orientation belong in presentation/mapping layers, not in ingress parsing.
- Toy routing should be deterministic and per-toy isolated.
- A failure or incomplete payload for one toy should not block other toys.
- Keep high-resolution support generic within the configured payload limit.

Common mapping/default context:

- `TopDownAlternateRightLeft` remains an important mapping mode.
- The app should not hardcode 32x8, though that remains a useful smoke-test size.

## Installer And Packaging

Current installer:

- project: `installer/VirtualDofMatrix.Installer`
- output: single-file `VirtualDofMatrix.Installer.exe`
- embeds app payload and release files
- installs an app folder shaped like the release ZIP:

```text
VirtualDofMatrix.App.exe
DOF/
instructions.html
```

Do not add legacy installer-system documentation or assumptions for current packaging work. Active packaging should target the standalone installer EXE and release ZIP.

Release workflow:

- workflow: `.github/workflows/manual-release.yml`
- stable releases use tags like `vX.Y.Z` and create GitHub Release entries
- internal test builds use tags like `test/vX.Y.Z-<suffix>` and upload workflow artifacts only
- release process documentation lives in `docs/RELEASES.md`

## Documentation Boundaries

- `docs/instructions.html` and `docs/README.md` are end-user facing.
- Keep tester/developer/agent planning content out of end-user docs.
- Use `docs/ARCHITECTURE.md` as the main technical reference for runtime architecture.
- Use `docs/DIRECTOUTPUT_VIRTUAL_DEVICE_PR_GUIDE.md` for porting the virtual DirectOutput controller behavior.
- Use this `AGENTS.md` for AI-agent/project context.

## Pre-Commit Agent Checklist

Before committing any change in this repository:

1. Add clear comments to newly added or heavily edited code blocks where intent would otherwise be non-obvious.
2. If settings changed, update `examples/settings.sample.json`.
3. If release behavior changed, update `docs/RELEASES.md`.
4. If setup or end-user behavior changed, update `docs/instructions.html` in the same commit.
5. Keep `docs/instructions.html` end-user focused.
6. Run focused tests or checks appropriate to the changed area, and report anything that could not be run.

## Useful Commands

```powershell
dotnet restore VirtualDofMatrix.sln
dotnet test VirtualDofMatrix.sln
dotnet publish src/VirtualDofMatrix.App/VirtualDofMatrix.App.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:PublishTrimmed=false
dotnet publish installer/VirtualDofMatrix.Installer/VirtualDofMatrix.Installer.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:InstallerAppPublishDir="src/VirtualDofMatrix.App/bin/Release/net8.0-windows/win-x64/publish/"
```

## If You Only Remember One Thing

This project is now a named-pipe virtual DOF matrix viewer with routing, multi-window presentation, and standalone EXE packaging. Preserve the separation between DirectOutput ingress, stream parsing, toy routing, and WPF rendering.
