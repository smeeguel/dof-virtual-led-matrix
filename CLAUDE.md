# Claude Code Project Brief

This file gives Claude Code contributors the project context needed before making changes in this repository.

## Project Summary

Virtual DOF Matrix is a Windows/.NET virtual LED matrix viewer for the Visual Pinball / DOF ecosystem.

It lets cabinet users see DOF LED output without physical LED hardware. The app receives frame data from DOF, routes the incoming RGB payload into one or more configured virtual toys, and renders those toys in WPF windows.

Current product goals:

- Windows-focused VPX/DOF companion app
- no LED hardware required
- named-pipe integration through this repo's virtual DirectOutput controller
- borderless, movable, resizable, always-on-top virtual toy windows
- low-overhead GPU-first rendering with CPU fallback
- persisted `settings.json` configuration
- redistributable ZIP and standalone installer EXE

## Read First

Use these files as the main orientation set:

- `AGENTS.md`: concise agent/project rules and current architecture summary
- `docs/ARCHITECTURE.md`: deeper runtime architecture and data flow
- `docs/CONFIG.md`: `settings.json` schema reference
- `docs/RELEASES.md`: manual release and internal test-build workflow
- `installer/README.md`: standalone installer build and output expectations
- `docs/DIRECTOUTPUT_VIRTUAL_DEVICE_PR_GUIDE.md`: DirectOutput virtual controller porting contract

Keep `docs/instructions.html` and `docs/README.md` end-user focused. Do not put contributor planning notes there.

## Current Runtime Model

The supported DOF integration path is named pipe output from the bundled/custom `DirectOutput.dll`.

```text
DOF VirtualLEDStripController -> named pipe VDMF frames -> VirtualDofMatrix.App -> routed toy windows/adapters
```

Important runtime facts:

- The app is the named-pipe server and should start before DOF initializes a table.
- Default frame pipe: `VirtualDofMatrix`
- Default control pipe: `VirtualDofMatrix.Control`
- DOF emits binary `VDMF` messages through `VirtualLEDStripController`.
- Removed prototype transports and legacy installer systems are not the active product path.

## Repository Structure

- `src/VirtualDofMatrix.App`
  - WPF application.
  - Startup, config persistence, transport host, control pipe, settings UI, viewer windows, and renderer selection.
- `src/VirtualDofMatrix.Core`
  - UI-agnostic models and logic.
  - Frame structures, matrix mapping, toy routing, config contracts, and compatibility defaults.
- `src/VirtualDofMatrix.Tests`
  - Unit tests for parsing, mapping, routing, config, rendering boundaries, and cabinet XML behavior.
- `installer/VirtualDofMatrix.Installer`
  - Standalone installer EXE.
  - Embeds the app payload and release payload.
- `DOF`
  - Runtime payload and example DirectOutput configuration content packaged with releases/installers.
- `tools`
  - CI, packaging, and helper scripts.
- `.github/workflows`
  - CI and manual release workflows.

Dependency direction:

```text
VirtualDofMatrix.App -> VirtualDofMatrix.Core
VirtualDofMatrix.Tests -> App/Core as needed
```

Keep `Core` independent from WPF/UI dependencies.

## Frame Flow

1. DOF loads this repo's virtual controller from `DirectOutput.dll`.
2. `VirtualLEDStripController` writes `VDMF` messages to a named pipe.
3. `FrameTransportHost` accepts the pipe connection.
4. `FrameTransportStreamParser` reconstructs messages from stream bytes.
5. `ToyRouter` maps incoming RGB payload ranges into configured toys.
6. Output adapters send routed toy frames to viewer windows or optional external sinks.
7. GPU or CPU renderers present frames in WPF windows.

Preserve the separation between ingress parsing, routing, output adapters, and rendering.

## VDMF Envelope

Each DirectOutput message is:

- ASCII magic: `VDMF`
- message type:
  - `1` = RGB payload frame
  - `2` = table/ROM metadata
- sequence: little-endian `Int32`
- payload length: little-endian `Int32`
- payload bytes

Parser behavior must remain stream-safe:

- support split reads
- support combined reads
- resynchronize after malformed data
- enforce payload limits
- survive disconnect/reconnect behavior

## Configuration Rules

Primary config file: `settings.json`

Sample config: `examples/settings.sample.json`

Main sections:

- `transport`: named pipe and control pipe names
- `matrix`: dimensions, mapping, renderer backend, dot shape, tone mapping, smoothing, bloom, and visual quality
- `window`: position, size, borderless/topmost/aspect behavior
- `debug`: protocol and frame logging
- `routing`: virtual toy definitions, source ranges, policies, and output targets
- `settings`: startup behavior, cabinet XML hints, and automation options

When config fields change, update `examples/settings.sample.json` in the same change.

## Routing And Rendering Principles

- Router output should stay in canonical linear RGB order.
- Mapping transforms belong in presentation/mapping layers, not in the transport parser.
- Routing should be deterministic and per-toy isolated.
- One broken or incomplete toy frame should not block other toys.
- Do not hardcode 32x8; it is a useful smoke-test size, not a product limit.
- Keep `TopDownAlternateRightLeft` working as an important mapping mode.

## Installer And Release Model

Current packaging targets:

- release ZIP containing app payload, `DOF/`, and `instructions.html`
- standalone installer EXE from `installer/VirtualDofMatrix.Installer`

Installed app folder shape:

```text
VirtualDofMatrix.App.exe
DOF/
instructions.html
```

Manual release workflow:

- `.github/workflows/manual-release.yml`
- `release_kind=stable` creates `vX.Y.Z` tags and GitHub Release entries
- `release_kind=test` creates `test/vX.Y.Z-<suffix>` tags and uploads workflow artifacts only
- test artifacts are split into app ZIP and installer EXE artifacts

Update `docs/RELEASES.md` when release behavior changes.

## Contribution Checklist

Before finishing a change:

1. Keep edits scoped to the requested behavior.
2. Preserve existing architecture boundaries unless the task explicitly requires changing them.
3. Add comments only where new or heavily edited logic is not obvious.
4. Update `examples/settings.sample.json` for config schema changes.
5. Update `docs/RELEASES.md` for release workflow changes.
6. Update `docs/instructions.html` only for end-user setup or behavior changes.
7. Run focused tests/checks for the touched area and report anything not run.

## Useful Commands

```powershell
dotnet restore VirtualDofMatrix.sln
dotnet test VirtualDofMatrix.sln
dotnet publish src/VirtualDofMatrix.App/VirtualDofMatrix.App.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:PublishTrimmed=false
dotnet publish installer/VirtualDofMatrix.Installer/VirtualDofMatrix.Installer.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true /p:InstallerAppPublishDir="src/VirtualDofMatrix.App/bin/Release/net8.0-windows/win-x64/publish/"
```

## Quick Mental Model

DOF writes framed RGB payloads to a named pipe. The app parses those frames, routes payload slices into configured virtual toys, and renders each toy through WPF output adapters. Keep that pipeline clean and decoupled.
