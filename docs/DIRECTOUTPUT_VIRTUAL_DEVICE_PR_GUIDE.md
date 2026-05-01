# DirectOutput virtual device support - upstream porting guide (fidelity-focused)

This document is the **authoritative porting spec** for reproducing this repository's DirectOutput changes in a fresh branch of upstream DirectOutput.

Goal: let a new engineer/agent apply the same behavior with no silent regressions.

---

## 1) What must be preserved

The upstream patch must preserve all of the following:

1. A new cabinet-XML-addressable controller type named `VirtualLEDStripController`.
2. A backward-compat alias type named `NamedPipeMatrixController`.
3. Named-pipe output transport (DOF as pipe client, external app as pipe server).
4. Binary `VDMF` frame envelope exactly as implemented here.
5. Message type `1` RGB frame emission for every `UpdateOutputs` call.
6. Message type `2` table/ROM metadata emission when context changes.
7. Existing hardware controller behavior unaffected.

If any of the above is omitted, functionality/fidelity diverges from this branch.

---

## 2) Minimal source patch inventory

Apply these code changes to upstream DOF:

### A. Add controller source file

- Add file: `DirectOutput/Cab/Out/AdressableLedStrip/VirtualLEDStripController.cs`

### B. Include file in project

- Update: `DirectOutput/DirectOutput.csproj`
- Add compile include entry for:
  - `Cab\Out\AdressableLedStrip\VirtualLEDStripController.cs`

No additional registration is required; DOF XML type loading resolves controller tags from compiled types.

---

## 3) Required class surface (must match)

`VirtualLEDStripController` inherits `OutputControllerCompleteBase`.

### Properties to expose

- `NumberOfLedsStrip1` ... `NumberOfLedsStrip10` (backed by `int[10]`)
- `PipeName` (default: `VirtualDofMatrix`)
- `ConnectTimeoutMs` (default: `2000`, bounded `100..30000`)
- `ReconnectDelayMs` (default: `250`, bounded `10..5000`)
- `FrameThrottleMs` (default: `0`, bounded `0..500`)

### Constructor default

- `NumberOfLedsPerStrip[0] = 256`

### Compatibility alias

- `public class NamedPipeMatrixController : VirtualLEDStripController { }`

Reason: older cabinet files using `<NamedPipeMatrixController>` must still load.

---

## 4) Runtime behavior contract

### 4.1 Configured output count

`GetNumberOfConfiguredOutputs()` must return:

- `NumberOfLedsPerStrip.Sum() * 3`

### 4.2 VerifySettings rules

`VerifySettings()` must fail and log warnings when:

- `PipeName` is null/whitespace.
- all strip counts are `<= 0`.
- any strip count is negative.

### 4.3 Connect/disconnect semantics

On connect:

- dispose prior pipe if present,
- open `NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.WriteThrough)`,
- call `Connect(ConnectTimeoutMs)`,
- reset sequence to zero,
- clear cached table/ROM last-published state.

On disconnect:

- best-effort publish metadata clear (`table=""`, `rom=""`),
- best-effort flush/dispose,
- null pipe reference,
- optional sleep `ReconnectDelayMs`.

### 4.4 UpdateOutputs semantics

`UpdateOutputs(byte[] OutputValues)` must:

1. throw if pipe is missing/disconnected,
2. publish table metadata if changed,
3. publish message type `1` with payload = `OutputValues` (`empty byte[]` if null),
4. apply optional `FrameThrottleMs` sleep.

---

## 5) Wire protocol spec (exact)

Each message is:

- `bytes 0..3`: ASCII `V D M F`
- `byte 4`: `messageType`
  - `1` = RGB frame
  - `2` = metadata
- `bytes 5..8`: `Int32` sequence, little-endian
- `bytes 9..12`: `Int32` payloadLength, little-endian
- `bytes 13..`: payload bytes

`sequence` increments per message (not per frame only).

### Metadata payload format (message type 2)

UTF-8 bytes of:

- `<tableName>\t<romName>\t`

Metadata source fields:

- `TableOverrideSettings.Instance.activetableName`
- `TableOverrideSettings.Instance.activeromName`

Publish metadata only when either value changes (case-insensitive comparison in current implementation).

---

## 6) Canonical source references in this repo

Use these files as behavior ground truth while porting:

- `DirectOutput-master/DirectOutput/Cab/Out/AdressableLedStrip/VirtualLEDStripController.cs`
- `DirectOutput-master/DirectOutput/DirectOutput.csproj`
- `DOF/Config/Cabinet.xml` (example controller usage)
- `src/VirtualDofMatrix.App/Transport/FrameTransportStreamParser.cs` (consumer expectations for `VDMF`)

---

## 7) Step-by-step upstream port procedure

1. Branch latest upstream `DirectOutput`.
2. Add `VirtualLEDStripController.cs` under `Cab/Out/AdressableLedStrip`.
3. Add the `.csproj` compile include.
4. Build x86 and x64.
5. Configure a local `Cabinet.xml` with `<VirtualLEDStripController>` and matching `<PipeName>`.
6. Start virtual viewer/pipe server first.
7. Start DOF host (VPX or test harness).
8. Confirm pipe connect + frame flow.
9. Validate metadata message emission on table transition.
10. Validate old tag `<NamedPipeMatrixController>` still works.

---

## 8) Acceptance checklist (functional fidelity)

All checks should pass before upstream PR:

- [ ] `VirtualLEDStripController` loads from XML without reflection/type errors.
- [ ] `NamedPipeMatrixController` alias loads and behaves identically.
- [ ] Invalid config values fail fast with logs.
- [ ] Connected path emits `VDMF` with correct little-endian header fields.
- [ ] Message type `1` payload bytes match outgoing DOF output bytes exactly.
- [ ] Message type `2` appears at first publish and on table/ROM changes.
- [ ] Disconnect sends clear metadata best-effort and disposes cleanly.
- [ ] Existing non-virtual controllers are unaffected in smoke tests.

---

## 9) Suggested upstream PR structure

### Commit 1 (required)

- controller file + project include,
- message type `1` support,
- docs/example for cabinet XML.

### Commit 2 (recommended, but optional if maintainer requests minimal patch)

- message type `2` metadata channel,
- metadata payload contract documentation.

If maintainers want smallest possible first merge, propose Commit 1 first and follow with Commit 2.

---

## 10) Example XML block (copy/paste)

```xml
<VirtualLEDStripController>
  <Name>LED Strips 0</Name>
  <NumberOfLedsStrip1>256</NumberOfLedsStrip1>
  <PipeName>VirtualDofMatrix</PipeName>
  <ConnectTimeoutMs>2000</ConnectTimeoutMs>
  <ReconnectDelayMs>250</ReconnectDelayMs>
</VirtualLEDStripController>
```

Then route `LedStrip` toys to this controller by `OutputControllerName`.

---

## 11) Non-goals for this DirectOutput patch

- No changes to DOF config tool schema/workflow required.
- No table-script changes required.
- No replacement of existing hardware controller paths.
- No dependency on external virtual-port tooling for this transport.

This is an additive virtual transport path.
