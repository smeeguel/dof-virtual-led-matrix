# DOF Non-COM Feasibility Report

Date: 2026-03-30

## Scope

This report evaluates whether DOF can be bridged to a virtual matrix viewer without virtual COM drivers and without patching DOF binaries.

## Key finding

Based on the uploaded source snapshot, the only **confirmed** startup/config integration path is Cabinet XML deserialization into built-in output controller types and subsequent runtime updates through `Cabinet.OutputControllers`.

A fully definitive answer on "external OutputController DLL loaded by config-only" is blocked by a snapshot gap: `DirectOutput/Cab/Out/*.cs` source files are referenced by the project but absent from the uploaded filesystem.

## What is proven in source

- B2S plugin initializes DOF through `Pinball.Setup(...); Pinball.Init();`.
- `Pinball.Setup` loads global config, cabinet XML, then table/ini config.
- `Cabinet` is deserialized with `new XmlSerializer(typeof(Cabinet)).Deserialize(...)`.
- Auto-config scans **already loaded assemblies** for `IAutoConfigOutputController` implementers using `AppDomain.CurrentDomain.GetAssemblies()`.
- `DirectOutput.csproj` statically compiles many built-in `Cab\Out\...` controllers (including `TeensyStripController`, `PinOne`, `PS`, `Pac`, `PhilipsHue`, etc.).
- `TeensyStripController` protocol and COM handshake sequence are explicit (`0x00`, `M`, `L`, `C`, `O`, `R`, `O`) and ACK-driven.

## Config/log evidence

- Current Cabinet uses `<TeensyStripController>` with `<ComPortName>COM1</ComPortName>` and matrix toy routing to that controller.
- Global config points to cabinet config and `directoutputconfig30.ini` in install `Config` folder.
- Runtime logs confirm repeated successful init of Teensy controller on COM1, handshake timing settings, and successful connect/disconnect lifecycle.

## Feasibility implications

### Non-destructive path confidence

- **High confidence** that replacing COM transport requires either:
  1. a new built-in-like controller type available at Cabinet XML deserialize time, or
  2. reusing an existing built-in non-COM-capable controller path.

- **Medium confidence** that external controller DLLs are *not* config-loadable out-of-the-box, because no dynamic assembly load/scan path was found in available startup code; however this cannot be fully closed without the missing `Cab/Out` serialization list source.

### Best additive candidate

- Investigate the existing built-in `PinOne` path first (project includes `Cab\Out\PinOne\NamedPipeServer.cs`), as this may provide a pre-existing non-COM transport seam without DOF patching.
- If `PinOne` protocol can be emulated from our viewer app, users could switch controller type in Cabinet XML (additive config-only migration).

## Recommended next validation steps

1. Recover missing `DirectOutput/Cab/Out/*.cs` files from canonical upstream to verify:
   - `OutputControllerList` XML attributes (`XmlElement`, `XmlArrayItem`, etc.),
   - any `Type.GetType`/reflection-based custom type activation,
   - any plugin/assembly probing logic for external controllers.
2. Decompile built `DirectOutput.dll` (same version as log: 3.2.9313.40083) if source recovery is not immediate.
3. If external controller loading is unsupported, test `PinOne` as a non-COM bridge target before proposing any DOF patch.

## Minimal fallback patch (if required)

If validation confirms no external controller loading:

- Add one minimal extension point in `OutputControllerList` deserialization/creation:
  - configurable assembly probe folder (e.g. `DirectOutput\Extensions`),
  - load assemblies at startup,
  - allow `<OutputController Type="Namespace.Type, Assembly">` form.
- Keep blast radius limited to controller construction path; avoid changing effect/toy/ini logic.
