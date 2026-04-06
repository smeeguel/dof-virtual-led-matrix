# Architecture

## Runtime overview

The shipped viewer is a WPF desktop app (`VirtualDofMatrix.App`) backed by shared core models/utilities (`VirtualDofMatrix.Core`) and unit tests (`VirtualDofMatrix.Tests`).

Current frame ingress is the custom DOF **named-pipe transport** (`VirtualLEDStripController` in the bundled `DirectOutput.dll`).

High-level runtime flow:

1. DOF writes framed matrix payloads to the configured pipe (default: `VirtualDofMatrix`).
2. `FrameTransportHost` accepts pipe clients and parses the VDMF envelope (`magic`, `version`, `sequence`, `payloadLength`, `payload`).
3. `FramePresentationDispatcher` marshals frames to the UI thread.
4. `MainWindow` maps logical LED ordering to display coordinates and submits frame colors to the active renderer backend.

## App layer responsibilities

- `App.xaml.cs`
  - startup/shutdown orchestration
  - settings load/save via `AppConfigurationStore`
  - command-mode handling (`--command show|hide|frontend-return|table-launch`)
  - control pipe server startup for runtime show/hide commands
- `Transport/FrameTransportHost.cs`
  - named-pipe server loop
  - framing validation and payload size guardrails
- `Presentation/FramePresentationDispatcher.cs`
  - thread-safe fan-out of incoming frame presentations

## Core layer responsibilities

- `AppConfig` and nested config models (`Transport`, `Matrix`, `Window`, `Debug`, `Settings`)
- matrix mapping/indexing helpers (`MatrixMapper`, `MatrixFrameIndexMap`)
- shared frame structures (`FramePresentation`, `FrameBuffer`, `Rgb24`)
- launch token parsing helpers (`PopperLaunchOptions`)

## Rendering pipeline

The renderer boundary is `IMatrixRenderer`, allowing runtime backend selection:

- `gpu` (default): `GpuInstancedMatrixRenderer`
  - instanced dot rendering path with GPU presentation modes
  - optional tone mapping / temporal smoothing / bloom controls
- `cpu`: `CpuMatrixRenderer`
  - software fallback path for compatibility/debug scenarios

`MainWindow` applies dot style and matrix geometry settings, then delegates per-frame updates through renderer methods:

- `Initialize(...)`
- `UpdateFrame(ReadOnlySpan<Rgb24>)`
- `Resize(...)`
- `Render()`
- `Dispose()`

## Safety and compatibility targets

- Practical LED compatibility ceiling remains aligned to DOF Teensy assumptions: **8 strips x 1100 LEDs = 8800 total LEDs**.
- Payload validation in transport rejects out-of-range frame sizes.
- Matrix geometry is configurable, but deployment assumptions remain VPX/DOF + Windows.
