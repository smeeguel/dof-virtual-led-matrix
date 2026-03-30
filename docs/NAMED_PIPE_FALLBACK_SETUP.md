# Named Pipe fallback setup (custom `DirectOutput.dll` build)

This is the fallback path when external extension loading is unavailable in stock DOF.

## 1) Build and replace DOF runtime DLL

Build the `DirectOutput` project from `DirectOutput-master/DirectOutput.sln` and deploy the resulting `DirectOutput.dll` into your DOF runtime `x64`/`x86` bin folder (matching your host process bitness).

The custom build in this repo adds `NamedPipeMatrixController`.

## 2) Switch cabinet controller type

Use the updated cabinet config in this repo (`dofinstall/Config/Cabinet.xml`), or mirror these key changes:

- Controller element: `<NamedPipeMatrixController>` (instead of `<TeensyStripController>`)
- Keep LED strip counts and controller name
- Set `<PipeName>VirtualDofMatrix</PipeName>`

## 3) Start viewer in named-pipe mode

`examples/settings.sample.json` now defaults to:

```json
"transport": {
  "mode": "namedPipe",
  "pipeName": "VirtualDofMatrix"
}
```

The app opens a `NamedPipeServerStream` and waits for DOF to connect.

## 4) Validate with logs

### Viewer-side expected logs

With `debug.logProtocol=true`, expected messages include:

- `Waiting for named pipe client on 'VirtualDofMatrix'...`
- `Named pipe client connected on 'VirtualDofMatrix'.`
- `Pipe frame seq=<n>, payload=<bytes> bytes.`

### DOF-side expected logs

In `DirectOutput.log`, expected lifecycle messages include the new controller type name:

- `NamedPipeMatrixController "LED Strips 0" initialized and updater thread started.`
- `... has connected to NamedPipeMatrixController "LED Strips 0".`

If viewer is not running when DOF starts, connect failure/reconnect warnings are expected.
