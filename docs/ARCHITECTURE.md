# Architecture and Runtime Flow (Agent/Developer Reference)

This document is the **repo-independent technical reference** for Virtual DOF Matrix.

It is intended for:
- AI agents that need enough context to generate user-facing docs/tutorials without full code access.
- Developers integrating, extending, or troubleshooting the app.

If you can only read one technical file before helping users, read this one.

---

## 1) Product definition and boundaries

Virtual DOF Matrix is a Windows desktop app that renders DOF LED output in one or more virtual windows.

Primary constraints:
- No custom DOF fork is required.
- Runtime is Windows-focused (VPX + DOF ecosystem).
- Current transport path is **named pipe** via a bundled `DirectOutput.dll` virtual controller.
- Internal payload limits are aligned to the current DOF-compatible ceiling: `8 strips * 1100 LEDs * 3 bytes RGB` max payload.

At runtime, the app can:
- receive frame payloads,
- route one incoming frame to multiple virtual toys,
- render toys into one or many WPF windows,
- optionally rebroadcast routed toy frames to other consumers.

---

## 2) Repository architecture map

Top-level projects:

- `src/VirtualDofMatrix.App`
  - WPF executable.
  - Owns startup lifecycle, config persistence, transport host, per-toy window orchestration, and renderer selection.

- `src/VirtualDofMatrix.Core`
  - Shared models and transformation logic.
  - Owns payload-to-toy routing rules, matrix mapping, frame structures, and config contracts.

- `src/VirtualDofMatrix.Tests`
  - Unit tests for parser, mapping, routing, config, and rendering boundary behaviors.

High-level dependency direction:

`VirtualDofMatrix.App` -> `VirtualDofMatrix.Core`

Tests reference both app/core where needed. Core is intentionally UI-agnostic.

---

## 3) End-to-end runtime flow

### 3.1 Ingress path

1. DOF (using this project’s `DirectOutput.dll`) publishes VDMF-framed messages to a named pipe.
2. `FrameTransportHost` accepts the pipe connection and reads bytes.
3. `FrameTransportStreamParser` incrementally parses the byte stream into complete frames.
4. Parsed payload frames are converted into routing context + routing plan.
5. `ToyRouter` maps raw payload bytes into one frame per configured toy.
6. Each routed toy frame is delivered to all enabled output adapters for that toy.
7. WPF adapter updates toy windows on the UI thread; optional adapters can broadcast externally.

### 3.2 Message envelope (VDMF)

The parser expects a binary envelope with:
- magic: `VDMF`
- message type (currently payload frame and table-context metadata)
- sequence
- payload length
- payload bytes

Important parser behaviors:
- Supports split reads and combined reads (stream-safe parsing).
- Keeps a rolling receive buffer.
- Resynchronizes on invalid headers instead of killing the session.
- Enforces payload length guardrails (`<= 8 * 1100 * 3`).

### 3.3 Connection sequence behavior

Each new named-pipe client connection increments an internal connection epoch.

Effective sequence used for routing diagnostics:
- `effectiveSequence = (connectionEpoch << 32) | rawSequence`

Why it exists:
- DOF may restart frame sequence numbers per reconnect.
- Epoching preserves monotonic ordering across reconnects for logging/policy logic.

---

## 4) App lifecycle and control model

`App.xaml.cs` is the orchestration root.

Startup sequence (simplified):

1. Resolve config path (fixed relative to executable for deterministic lookup).
2. Clear launch log buffer and load persisted settings.
3. Bootstrap config folder state and active table scope.
4. Handle command-only mode (`show`, `hide`, `frontend-return`, `table-launch`) if requested.
5. Construct main window + settings hooks.
6. Construct routing pipeline:
   - `ConfigRoutingPlanProvider`
   - `ToyRouter`
   - output adapters (`WpfWindowOutputAdapter`, `NamedPipeBroadcastAdapter`)
7. Start `FrameTransportHost`.
8. Start control server for runtime commands.
9. Apply startup visibility.

Shutdown sequence:
- cancel control server,
- stop transport host,
- persist window/config,
- dispose adapters,
- exit WPF app cleanly.

This ordering avoids dangling background tasks and stale persistence writes.

---

## 5) Routing model (core behavior)

Routing is intentionally deterministic and per-toy isolated.

### 5.1 Inputs to routing

`ToyRouter.Route(...)` consumes:
- current payload bytes,
- `RoutingFrameContext` metadata,
- active `ToyDefinition` list.

### 5.2 Per-toy processing

For each toy, router:

1. Skips disabled toys.
2. Applies frame-rate policy checks (`latest-wins`, `drop-oldest`, etc.).
3. Computes source range from canonical start + length.
4. Copies available RGB bytes into a per-toy pixel buffer.
5. Applies policy for missing bytes:
   - drop frame,
   - hold last frame,
   - pad with black.
6. Emits `ToyFrame` + diagnostic event.

Design principle:
- failure in one toy should not block other toys (`per-toy failure isolation`).

### 5.3 Canonical ordering rule

Router outputs toy pixels in canonical linear order.

It intentionally does **not** apply visual rotations/serpentine transforms during routing.
Those transforms happen in downstream presentation/mapping layers so mapping is applied once.

---

## 6) Presentation and window orchestration

### 6.1 Output adapter abstraction

Routing publishes to adapters implementing a shared output contract.

Current adapters include:
- `viewer` adapter (`WpfWindowOutputAdapter`) for on-screen toy windows.
- named-pipe broadcast adapter for external consumers.

A toy can target one adapter, multiple adapters, or none (if disabled).

### 6.2 Viewer adapter responsibilities

`WpfWindowOutputAdapter`:
- keeps one binding per toy (`ToyWindowBinding`),
- reuses `MainWindow` for the selected primary toy,
- creates secondary windows for additional viewer-targeted toys,
- marshals writes to Dispatcher/UI thread,
- controls visibility/focus/hover/select/edit overlays,
- persists geometry changes through callbacks to app config store.

This enables multi-window virtual toy layouts while keeping the main host window stable.

### 6.3 Legacy dispatcher note

`FramePresentationDispatcher` remains in the codebase for compatibility, but live flow is adapter-first.
`FrameTransportHost` routes directly to adapters in the current architecture.

---

## 7) Rendering pipeline

Renderer boundary is `IMatrixRenderer`.

Available backends:
- `GpuInstancedMatrixRenderer` (default/high-performance path)
- `CpuMatrixRenderer` (fallback/compatibility path)

Main responsibilities in rendering stack:
- initialize geometry and visual profile,
- update per-frame RGB data,
- handle resize events,
- present frame,
- dispose graphics resources.

Visual tunables (from config) include style and quality knobs such as dot shape, off-state behavior, bloom profile, and present mode.

---

## 8) Configuration model and persistence

Primary persisted config is `settings.json` using `AppConfig` schema.

Major groups:
- `transport` (pipe name, transport-level behavior)
- `matrix` (dimensions, renderer/backend, visuals)
- `window` (position, size, topmost, borderless behavior)
- `debug` (protocol/frame logging flags)
- `routing` (toy definitions, policies, output targets)
- `settings` (startup/automation behaviors)

Persistence principles:
- load early on startup,
- save after effective state changes,
- debounce window position/size persistence,
- keep deterministic config path behavior for front-end launches.

---

## 9) Table/context awareness

Transport accepts metadata messages (message type for context payload) carrying table/ROM hints.

`FrameTransportHost` publishes these as `TableContextMetadata` events.

App uses table context to support:
- scope-aware toy visibility/override resolution,
- better startup defaults in front-end workflows,
- clearer diagnostics.

---

## 10) Safety, limits, and failure handling

### Guardrails
- Parser rejects malformed headers and oversized payloads.
- Routing policies make missing/partial data explicit.
- Connection-level exceptions are contained; host loop continues listening.

### Logging behavior
- Protocol logging can be toggled.
- First clean route per toy is logged to keep logs actionable rather than noisy.
- Frame-level logging can be enabled for deeper debugging.

### Performance stance
- Streaming parser avoids per-read packet assumptions.
- UI writes are dispatcher-marshaled and adapter-scoped.
- Routing does not block on one toy’s error path.

---

## 11) Practical troubleshooting flow (for support docs)

When users report “no matrix output,” check in this order:

1. App is running before table launch.
2. Pipe name in app config matches DOF virtual controller config.
3. Protocol logs show pipe client connection.
4. Parser receives valid VDMF frames (no persistent header drops).
5. Routing plan has at least one enabled toy with matching output target.
6. Toy visibility is enabled for current scope (global/table override).
7. Renderer backend is valid on that machine (switch GPU/CPU to isolate).

This seven-step ladder covers most real-world failures.

---

## 12) Extension points and future evolution

Current design intentionally leaves clear seams for expansion:

- Add new output adapters (shared memory, websocket, file dump) without changing router semantics.
- Add new routing strategies/policies while preserving per-toy isolation guarantees.
- Add additional transport sources if needed (keeping parser and router decoupled).
- Evolve table-aware behavior without entangling renderers.

Core architectural rule to preserve:

**Ingress parsing, frame routing, and visual presentation should remain separable layers.**

That separation is what makes the app maintainable and LLM-documentable.

---

## 13) Quick glossary

- **VDMF**: Framed named-pipe message format used between DOF virtual controller and app.
- **Toy**: A virtual output surface (strip/matrix) with geometry + source mapping + targets.
- **Routing plan**: Active list of toy definitions used by router for current scope.
- **Canonical order**: Linear LED ordering from payload before display mapping transforms.
- **Output adapter**: Pluggable sink that consumes routed toy frames (viewer, broadcast, etc.).

---

## 14) “If you are an LLM” summary block

If you need to write user docs without repo access, keep this mental model:

- DOF writes framed RGB payloads to a named pipe.
- App parser reconstructs frames from stream bytes robustly.
- Router slices one payload into per-toy frames using configured ranges/policies.
- Output adapters deliver each toy frame to windows and optional external sinks.
- WPF viewer windows render toy frames using GPU or CPU backend.
- Config + table scope decide which toys are visible and where they appear.

That is the complete flow from DOF signal to on-screen virtual LEDs.
