# Named-pipe DOF integration setup

> This repository currently ships and documents the **named-pipe integration path** using the custom `DirectOutput.dll` build that includes `VirtualLEDStripController`.

## 1) Deploy DOF runtime DLLs from this repo/package

Copy the provided `DirectOutput.dll` files into your DOF runtime folders:

- `C:\DirectOutput\x64\DirectOutput.dll`
- `C:\DirectOutput\x86\DirectOutput.dll`

Back up existing DLLs first.

## 2) Use `VirtualLEDStripController` in `Cabinet.xml`

Your cabinet configuration must target the virtual controller type (not `TeensyStripController` for this build path):

- controller element: `<VirtualLEDStripController>`
- pipe name field: `<PipeName>VirtualDofMatrix</PipeName>` (or match your custom setting)
- keep your matrix dimensions/arrangement aligned with your viewer config

## 3) Configure `settings.json`

The app listens on the transport pipe from `settings.json`:

```json
"transport": {
  "pipeName": "VirtualDofMatrix",
  "controlPipeName": "VirtualDofMatrix.Control"
}
```

If you change `PipeName` in `Cabinet.xml`, update `settings.json` to the same name.

## 4) Run order

1. Start `VirtualDofMatrix.App.exe` first.
2. Start frontend/VPX so DOF initializes and connects.

## 5) Verify connectivity in logs

When protocol logging is enabled (`debug.logProtocol=true`), expected viewer logs include:

- `Waiting for named pipe client on 'VirtualDofMatrix'...`
- `Named pipe client connected on 'VirtualDofMatrix'.`
- Optional frame logs when `debug.logFrames=true`:
  - `Pipe frame seq=<n>, payload=<bytes> bytes.`

DOF-side `DirectOutput.log` should show `VirtualLEDStripController` init/connect lifecycle entries.

## 6) Troubleshooting quick checks

- Pipe names mismatch between `Cabinet.xml` and `settings.json`.
- Viewer not started before DOF attempts initial connect.
- Wrong DOF DLL bitness deployed for the host process.
- Old cached DOF config still active (restart frontend/table after config changes).
