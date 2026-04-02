# USER.md

## Purpose

This document provides a practical developer-facing blueprint for the current Virtual DOF Matrix direction:

1. Build and maintain the viewer/runtime around the named-pipe DOF integration path.
2. Track open technical/product questions.
3. Standardize local build, run, and test workflows.

---

## 1) Build Plan: Viewer + Frame Ingestion

## Guiding constraints

- Keep DirectOutput integration intact.
- Use the `VirtualLEDStripController` named-pipe path.
- Keep compatibility target at **8 strips x 1100 LEDs = 8800 LEDs**.
- Decouple frame ingestion/parsing from rendering.
- Keep runtime overhead low and packaging simple for community sharing.

## Target architecture

- **Solution**: `.NET 8`.
- **Projects**:
  - `VirtualDofMatrix.Core` (frame model, mapping, config model, rendering helpers).
  - `VirtualDofMatrix.App` (WPF UI, named-pipe host, app lifecycle, settings persistence).
  - `VirtualDofMatrix.Tests` (unit tests for mapping/rendering/protocol utilities).

## Milestone plan

### Milestone 1 — Frame ingestion core

Goal: reliable named-pipe frame ingestion and presentation handoff.

Implement:

- Deterministic frame envelope parsing.
- Version checks and payload validation.
- Explicit ingestion logs (connection state, sequence numbers, payload sizes).

Deliverables:

- Known-good payloads are received and stored as the latest frame.
- Stable reconnect behavior when DOF restarts.

### Milestone 2 — Viewer window (WPF)

Goal: render received frame in a borderless utility window.

Implement:

- Borderless, always-on-top, movable, resizable window.
- Render loop bound to latest frame snapshot.
- Basic visual options:
  - Dot shape: square/circle.
  - Pixel spacing and size.
  - Brightness/gamma multiplier.
- Persist window geometry + display options in JSON.

### Milestone 3 — Generic matrix geometry + mapping

Goal: support beyond 32x8 while preserving payload fidelity.

Implement:

- Configurable matrix `width`, `height`.
- Mapping modes:
  - Row-major
  - Column-major
  - Serpentine alternate direction
  - Explicit support for `TopDownAlternateRightLeft`

### Milestone 4 — Hardening + UX polish

Implement:

- Diagnostics/log output improvements.
- Optional tray icon and startup behavior.
- Better recoverability around disconnect/reconnect.
- Config validation with user-friendly errors.

### Milestone 5 — Packaging + community readiness

Implement:

- Self-contained Windows publish profile (`win-x64`).
- Quick-start docs aligned to named-pipe runtime.
- Release zip suitable for non-developers.

---

## 2) Open Questions

1. Should the viewer present every received frame immediately, or coalesce bursts under load?
2. Should frame buffering retain a short history for debug playback?
3. Should mapping selection be hot-swappable at runtime or applied on restart?
4. For high LED totals (e.g., 8192), should CPU mode remain available by default?
5. What logging defaults are safe for non-technical users without large log files?

---

## 3) Dev-side Tools + Build / Run

## Required tooling

- **Windows machine** (target runtime environment).
- **.NET SDK 8.x**.
- **IDE/editor**:
  - Visual Studio 2022 (recommended for WPF), or
  - VS Code + C# extensions.
- **Git**.

## Setup steps (from repo root)

```bash
dotnet --info
dotnet restore VirtualDofMatrix.sln
dotnet build VirtualDofMatrix.sln -c Debug
dotnet test src/VirtualDofMatrix.Tests/VirtualDofMatrix.Tests.csproj -c Debug
```

## Run command

```bash
dotnet run --project src/VirtualDofMatrix.App/VirtualDofMatrix.App.csproj
```

## Publish command (redistributable)

```bash
dotnet publish src/VirtualDofMatrix.App/VirtualDofMatrix.App.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```
