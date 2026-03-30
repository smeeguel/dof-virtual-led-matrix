# DOF Non-COM Integration Feasibility Report (main branch snapshot)

Date: 2026-03-30

## Scope and method

This report analyzes the source and sample runtime artifacts currently in the repository:

- `DirectOutput-master` (DOF source snapshot)
- `DirectOutput-config-logs` (runtime config + logs)
- `dofinstall/Config` (working local config set including `directoutputconfig30.ini`)

The goal is to determine whether we can get matrix output from DOF to our viewer **without virtual COM** and **without modifying DOF binaries**, and only define a DOF patch as last resort.

---

## A) Inventory and orientation

### Located folders

- `DirectOutput-master`
- `DirectOutput-config-logs`

### DOF solution/projects and runtime-relevant assemblies

- DOF solution file: `DirectOutput-master/DirectOutput.sln`.
- Main framework assembly project: `DirectOutput-master/DirectOutput/DirectOutput.csproj` (`<AssemblyName>DirectOutput</AssemblyName>`).
- B2S plugin bridge project: `DirectOutput-master/B2SServerPlugin/B2SServerPlugin.csproj` (`<AssemblyName>B2SServerDirectOutputPlugin</AssemblyName>`).
- The B2S plugin implementation (`B2SServerPlugin/Plugin.cs`) instantiates `DirectOutput.Pinball` and forwards `DataReceive(...)` into DOF (`Pinball.ReceiveData(...)`).

### Where output controllers are defined and instantiated

- Output controller contract: `DirectOutput.Cab.Out.IOutputController`.
- Controllers are materialized from XML in `OutputControllerList.ReadXml(...)`.
- `Cabinet.GetCabinetFromConfigXml(...)` deserializes `Cabinet`, which triggers output-controller list deserialization.
- Runtime use path:
  - `Pinball.Init()` loads cabinet config,
  - `Cabinet.Init()` calls `OutputControllers.Init(this)`,
  - `Cabinet.Update()` calls `OutputControllers.Update()` each cycle.

---

## B) Can external/custom OutputControllers be added without patching DOF?

## Short answer

**Not via config-only with the current code path.**

### What the code does (confirmed)

1. `OutputControllerList.ReadXml(...)` resolves controller XML tags by looking up `Type.Name` from a `TypeList` built from:

```csharp
AppDomain.CurrentDomain.GetAssemblies()
    .SelectMany(s => s.GetTypes())
    .Where(p => typeof(IOutputController).IsAssignableFrom(p) && !p.IsAbstract)
```

2. `TypeList` lookup is by simple type name (`Type.Name == reader.LocalName`), not assembly-qualified type name.
3. There is no discovered code path in DOF that scans a plugin/extensions folder and dynamically `Assembly.Load*()`s arbitrary controller assemblies before this deserialization.

### Why that blocks config-only external controllers

- Cabinet XML can only reference a type already present in the current AppDomain's loaded assemblies.
- A standalone custom controller DLL is not loaded just because its class name appears in XML.
- Since no external assembly discovery/load hook is present, a drop-in DLL + XML tag alone is insufficient.

### Practical implication

- **Config-only add-in controller:** not supported by current source.
- **Possible non-fork workaround:** replace `DirectOutput.dll` with a build that includes the new controller class (single binary swap, still user-visible DOF binary replacement).

---

## C) Current config/runtime behavior (from artifacts)

### Cabinet/global config path

- `GlobalConfig_B2SServer.xml` points cabinet config to `{InstallDir}\Config\Cabinet.xml`.
- It uses table config pattern `{InstallDir}\Config\directoutputconfig30.ini`.

### Active Teensy config

- `Cabinet.xml` defines one `<TeensyStripController>` named `LED Strips 0` with `ComPortName=COM1`.
- It defines a `LedStrip` toy mapped to `OutputControllerName=LED Strips 0`.

### Runtime confirmation from log

- DOF version/build banner confirms active runtime and pathing.
- Cabinet load succeeds repeatedly with `1 output controller definitions and 2 toy definitions loaded`.
- Teensy controller initializes, opens COM1 at 9600 8N1, and reports successful connection.
- Session shutdown cleanly disconnects controller.

### About protocol sequence (`00`, `M`, `L`, `C`, `O`, `R`, `O`)

- The sampled DOF log does **not** print low-level serial payload bytes/commands.
- Source code in `TeensyStripController` confirms this exact handshake/update sequence:
  - command-mode probe `0x00` expecting `'A'`/`'N'`,
  - setup `M`, then `L`, then `C`, then `O`,
  - frame updates via `R` packets plus final `O`, with ACK checks after writes.

So the sequence is source-confirmed and consistent with your prior capture, but **not directly byte-logged** in `DirectOutput.log`.

---

## D) Non-COM transport options (ranked)

## Rank #1 (recommended): Custom DOF OutputController -> Named Pipe -> Viewer

### Feasibility

- Feasible **if** controller code is in loaded DOF assembly (currently implies DirectOutput.dll replacement build or upstream acceptance).
- Named pipes are local, no driver install, no admin friction, low latency, reliable framing.

### User complexity

- Moderate if delivered as replacement `DirectOutput.dll` + updated `Cabinet.xml` controller tag.
- Low runtime complexity for users once installed.

### Latency/reliability

- Very good for same-machine IPC; ordered, reliable stream.

### Compatibility risk

- Main risk is DOF binary swap/version drift, not transport itself.

## Rank #2: Custom DOF OutputController -> Loopback TCP (or WebSocket) -> Viewer

### Feasibility

- Same extension constraint as above (controller class must be loaded by DOF).
- Transport implementation straightforward in .NET.

### User complexity

- Slightly higher (possible firewall prompts, port conflicts).

### Latency/reliability

- Good on loopback; slightly more moving pieces vs named pipes.

### Compatibility risk

- Moderate (network stack/policy interference, AV/firewall noise).

## Rank #3: Alternate upstream hook (B2S plugin-side event bridge)

### Feasibility

- Technically feasible to build a separate B2S plugin that receives table events (`DataReceive`) and forwards to viewer.
- But this sits **upstream of DOF effects/mapping**, so reproducing final matrix behavior means reimplementing major DOF logic (large scope, high drift risk).

### User complexity

- Potentially high (B2S plugin management and unknown coexistence behavior with DirectOutput plugin in end-user setups).

### Latency/reliability

- Event delivery likely fine; semantic fidelity to DOF output is poor unless a large stack is reimplemented.

### Compatibility risk

- High for behavior parity with DOF table configs.

---

## E) Minimal fallback patch if non-destructive extension is blocked

Because config-only external controllers are blocked by type discovery/load behavior, the smallest surgical enablement is:

1. Add one external assembly loader at startup (before cabinet XML deserialization), e.g.:
   - load `*.dll` from `{InstallDir}\Extensions` via `Assembly.LoadFrom(...)`.
2. Keep existing `OutputControllerList.ReadXml(...)` unchanged; once assembly is loaded, its `IOutputController` type appears in `AppDomain.CurrentDomain.GetAssemblies()` and XML tag resolution works.

### Minimal blast radius

- Primary touchpoint: startup path before `Cabinet.GetCabinetFromConfigXmlFile(...)` in `Pinball.Init()`.
- Optional helper location: `DirectOutputHandler` or a small new utility class for extension loading.

### User experience under fallback

- Still mostly additive from user perspective:
  - one updated DOF binary (`DirectOutput.dll`),
  - drop custom controller DLL into `Extensions` folder,
  - add controller tag in `Cabinet.xml`.

---

## F) MVP blueprint for this repo

## Recommended architecture

### Phase 1 (fastest practical): embedded controller in a DOF-compatible replacement build

- Implement `NamedPipeMatrixController : OutputControllerCompleteBase` inside DOF codebase (or a maintained patched build).
- Preserve existing matrix toy mapping in cabinet/table config path.
- Stream final RGB data directly to viewer process over named pipe.

### Core contract (DOF side <-> viewer side)

- **Session handshake**
  - Viewer sends hello/version + capabilities.
  - DOF-side controller replies accepted protocol version + matrix metadata.
- **Frame packet schema (binary)**
  - Header: magic, protocolVersion, messageType, sequence, payloadLength.
  - Metadata message: width, height, colorOrder, mapping mode, brightness scalar (optional).
  - Frame message: packed RGB bytes in logical LED order used by current `LedStrip` output.
- **Ack/error behavior**
  - Optional ACK every N frames (for diagnostics only), not required per-frame to avoid stalling DOF.
  - Error message types for malformed packets/version mismatch.
- **Reconnect semantics**
  - Non-blocking send path; if pipe disconnects, controller keeps running and retries connect on interval.
  - Viewer can restart independently.

### Mapping existing `M/L/C/R/O` concepts to pipe transport

- Keep these semantics internal to DOF controller lifecycle:
  - `M/L` equivalent -> initial metadata negotiation.
  - `C` equivalent -> explicit clear-frame message.
  - `R` equivalent -> one or more frame data chunks.
  - `O` equivalent -> frame-present/commit marker (or implicit per frame packet).

### App config additions (viewer)

- `transport.mode`: `namedPipe|tcp`
- `transport.pipeName` (default `VirtualDofMatrix`)
- `transport.reconnectMs`
- `transport.expectWidth/expectHeight` (optional validation)
- logging verbosity and binary dump toggles.

### User troubleshooting checklist

- Confirm DOF log shows custom controller initialized.
- Confirm viewer log shows successful handshake + protocol version.
- Confirm frame counter increments while table runs.
- Confirm matrix dimensions in DOF and viewer agree.
- If blank output: validate `Cabinet.xml` controller name binding and `LedWizEquivalent` mapping to matrix toy.

---

## Optional Cabinet.xml snippet (if/when custom controller type is available)

```xml
<OutputControllers>
  <NamedPipeMatrixController>
    <Name>Matrix Pipe 0</Name>
    <PipeName>VirtualDofMatrix</PipeName>
    <ReconnectIntervalMs>1000</ReconnectIntervalMs>
  </NamedPipeMatrixController>
</OutputControllers>

<Toys>
  <LedStrip>
    <Name>Matrix1</Name>
    <Width>64</Width>
    <Height>16</Height>
    <LedStripArrangement>TopDownAlternateRightLeft</LedStripArrangement>
    <ColorOrder>RGB</ColorOrder>
    <FirstLedNumber>1</FirstLedNumber>
    <OutputControllerName>Matrix Pipe 0</OutputControllerName>
  </LedStrip>
</Toys>
```

This snippet assumes the controller type is discoverable in the AppDomain (currently true only if built into/loaded by DOF process).

---

## Final feasibility conclusion

- **Best non-destructive path (strictly no DOF binary changes): not currently proven viable for custom output controllers.**
- **Best practical path:** minimal DOF patch (or upstreamed change) to support external controller assembly loading, then use named pipe controller as additive drop-in.
- **If patching remains disallowed:** continue COM emulation path for full DOF fidelity, or accept lower-fidelity upstream event bridge approach.
