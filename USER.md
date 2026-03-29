# USER.md

## Purpose

This document translates the project direction in `AGENTS.md` into a practical developer-facing blueprint for:

1. Building the DOF-compatible serial emulator and visual client.
2. Tracking open technical/product questions.
3. Standardizing local dev setup, build, run, and test workflows.

---

## 1) Build Plan: Emulator + Visual Client

## Guiding constraints

- Do **not** fork or modify DOF.
- Emulate a DOF-supported serial device (Teensy strip controller behavior).
- Keep compatibility target at **8 strips x 1100 LEDs = 8800 LEDs**.
- Decouple protocol parsing from rendering.
- Keep runtime overhead low and package for easy community distribution.

## Target architecture

- **Solution**: `.NET 8`.
- **Projects**:
  - `VirtualDofMatrix.Core` (protocol/state machine, frame model, mapping, config model).
  - `VirtualDofMatrix.App` (WPF UI, serial port hosting, app lifecycle, settings persistence).
  - `VirtualDofMatrix.Tests` (unit + integration-ish parser tests).
- **Optional later**: CLI/headless mode for protocol diagnostics.

## Milestone plan

### Milestone 1 — Protocol emulator core (console-compatible)

Goal: reliable TeensyStripController serial emulation on COM port.

Implement:

- Deterministic parser/state machine with a single RX buffer.
- Command handling:
  - `0x00` -> `A`
  - `M` -> `max_hi max_lo A`
  - `L + 2 bytes` -> `A`
  - `C` -> `A`
  - `O` -> `A`
  - `R + header + payload` -> `A`
- Configurable serial settings (port, baud, max LEDs).
- Explicit protocol logs (bytes in/out, parsed intent, state transitions).

Deliverables:

- Pass minimum sequence test (`00, M, L, C, O, R, O`).
- Successfully receives and stores latest frame payload (e.g., 256 LEDs / 768 bytes).

### Milestone 2 — Viewer window (WPF)

Goal: render received frame in a borderless utility window.

Implement:

- Borderless, always-on-top, movable, resizable window.
- Render loop bound to latest frame snapshot.
- Basic visual options:
  - Dot shape: square/circle.
  - Pixel spacing and size.
  - Brightness/gamma multiplier.
  - Nearest-neighbor style scaling.
- Persist window geometry + display options in JSON.

Deliverables:

- Live updates on `O` (or selected present trigger).
- Position/size restored on app restart.

### Milestone 3 — Generic matrix geometry + mapping

Goal: support beyond 32x8 while preserving protocol fidelity.

Implement:

- Configurable matrix `width`, `height`.
- Mapping modes:
  - Row-major
  - Column-major
  - Serpentine alternate direction
  - Explicit support for `TopDownAlternateRightLeft`
- Keep raw DOF payload order; mapping is a separate transform layer.

Deliverables:

- Verified rendering for at least:
  - `32x8` (baseline)
  - `64x32`
  - one serpentine case

### Milestone 4 — Hardening + UX polish

Implement:

- Protocol/session diagnostics panel (or rich log file output).
- Optional tray icon and startup behavior.
- Better recoverability (COM disconnect/reconnect strategy).
- Config validation with user-friendly errors.

Deliverables:

- Stable operation across repeated DOF reconnects and malformed input edge cases.

### Milestone 5 — Packaging + community readiness

Implement:

- Self-contained Windows publish profile (`win-x64`).
- Example config and docs for VSPE COM-pair setup (`COM1 <-> COM2`).
- Quick-start README with troubleshooting.

Deliverables:

- Shareable release zip usable by non-developers.

## Suggested implementation order (first coding sprint)

1. Create `.NET 8` solution + three projects.
2. Implement `Core` parser and protocol response contract.
3. Add parser tests for split/combined reads and payload sizing.
4. Add serial host in App and wire to Core event callbacks.
5. Render latest frame in WPF surface.
6. Add JSON settings load/save.
7. Add structured logging and docs.

---

## 2) Open Questions

## Protocol/compatibility questions

1. Is `O` always the only frame-present signal we should treat as “display now,” or do some table/config combos require immediate update after `R`?
2. Should `R` updates be applied into per-strip buffers first and flattened only on `O` for strict parity with hardware behavior?
3. Do we need to support multi-strip updates in MVP, or is one logical strip sufficient for first release?
4. Should we actively reject configurations above 8 strips/1100 LEDs in config validation, or allow expert override with warnings?
5. What exact serial timeouts/retry behavior best matches DOF expectations in noisy COM environments?

## Mapping/rendering questions

6. What is the canonical coordinate definition for `TopDownAlternateRightLeft` in this project (document with examples)?
7. Should mapping be hot-swappable at runtime or only on restart?
8. For high LED counts (e.g., 8192), do we target CPU drawing first, or move directly to a GPU-backed rendering path if needed?

## Product/UX questions

9. Do we need a settings UI in MVP, or is JSON-only configuration acceptable for first public release?
10. Should click-through / transparent overlay behavior be milestone 4 or deferred until after first release?
11. What logging defaults are safe for normal users without creating oversized log files?

## Packaging/support questions

12. Which Windows versions are officially supported for redistribution (Windows 10 only vs 10/11)?
13. How should updates be delivered (manual zip replacement vs installer)?
14. Do we include sample `Cabinet.xml` templates for common matrix sizes in the first release package?

---

## 3) Dev-side Tools + Environment Setup / Build / Run

## Required tooling

- **Windows machine** (target runtime environment).
- **.NET SDK 8.x**.
- **IDE/editor**:
  - Visual Studio 2022 (recommended for WPF), or
  - VS Code + C# extensions.
- **Git**.
- **VSPE** (or equivalent virtual COM pair utility) for local DOF loop testing.

## Optional but recommended

- Serial monitor utility for low-level COM verification.
- Hex viewer/log viewer for protocol capture inspection.
- xUnit test runner integration in IDE.

## Environment assumptions

Initial known-good dev/test topology:

- DOF side: `COM1`
- Emulator side: `COM2`

Keep these as defaults, but configurable via JSON.

## Suggested repository layout to implement

```text
/
  USER.md
  README.md
  docs/
    PROTOCOL.md
    ARCHITECTURE.md
    CONFIG.md
  src/
    VirtualDofMatrix.Core/
    VirtualDofMatrix.App/
    VirtualDofMatrix.Tests/
  examples/
    settings.sample.json
    Cabinet.xml
```

## Setup steps (from repo root)

```bash
dotnet --info
dotnet new sln -n VirtualDofMatrix
dotnet new classlib -n VirtualDofMatrix.Core -o src/VirtualDofMatrix.Core
dotnet new wpf -n VirtualDofMatrix.App -o src/VirtualDofMatrix.App
dotnet new xunit -n VirtualDofMatrix.Tests -o src/VirtualDofMatrix.Tests
dotnet sln VirtualDofMatrix.sln add src/VirtualDofMatrix.Core/VirtualDofMatrix.Core.csproj
dotnet sln VirtualDofMatrix.sln add src/VirtualDofMatrix.App/VirtualDofMatrix.App.csproj
dotnet sln VirtualDofMatrix.sln add src/VirtualDofMatrix.Tests/VirtualDofMatrix.Tests.csproj
dotnet add src/VirtualDofMatrix.App/VirtualDofMatrix.App.csproj reference src/VirtualDofMatrix.Core/VirtualDofMatrix.Core.csproj
dotnet add src/VirtualDofMatrix.Tests/VirtualDofMatrix.Tests.csproj reference src/VirtualDofMatrix.Core/VirtualDofMatrix.Core.csproj
```

## Build/test commands

```bash
dotnet restore VirtualDofMatrix.sln
dotnet build VirtualDofMatrix.sln -c Debug
dotnet test src/VirtualDofMatrix.Tests/VirtualDofMatrix.Tests.csproj -c Debug
```

## Run commands

App run:

```bash
dotnet run --project src/VirtualDofMatrix.App/VirtualDofMatrix.App.csproj
```

Optional headless protocol mode (if implemented):

```bash
dotnet run --project src/VirtualDofMatrix.App/VirtualDofMatrix.App.csproj -- --headless --config examples/settings.sample.json
```

## Publish commands (redistributable)

```bash
dotnet publish src/VirtualDofMatrix.App/VirtualDofMatrix.App.csproj -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

## Baseline test matrix (dev checklist)

1. Handshake: `0x00 -> A`.
2. Setup: `M`, `L`, `C`, `O` command acknowledgements.
3. Runtime: `R` parse for 256 LEDs (768 bytes).
4. Parser robustness:
   - combined reads (command + payload in one read),
   - split reads (payload fragments over many reads).
5. Viewer refresh correctness on frame output trigger.
6. Config persistence (window geometry + render options).

## Logging guidance

- Keep protocol logging human-readable with timestamps.
- Separate high-volume frame logs behind a config flag.
- Include a startup summary of effective settings (port, baud, matrix, mapping).

## Definition of done for first public MVP

- Works with DOF over virtual COM pair without DOF modifications.
- Correctly emulates required Teensy command set.
- Renders live matrix output in borderless always-on-top window.
- Persists key user settings.
- Includes docs + sample config sufficient for community setup.
