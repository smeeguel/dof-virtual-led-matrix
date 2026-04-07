# Config reference (`settings.json`)

## Top-level sections

- `transport`
- `matrix`
- `window`
- `debug`
- `settings`

An up-to-date runnable baseline is provided in `examples/settings.sample.json`.

---

## `transport`

```json
"transport": {
  "pipeName": "VirtualDofMatrix",
  "controlPipeName": "VirtualDofMatrix.Control"
}
```

- `pipeName`: frame ingress named pipe (DOF `VirtualLEDStripController` writes frames here).
- `controlPipeName`: optional runtime control channel for show/hide commands.

---

## `matrix`

```json
"matrix": {
  "renderer": "gpu",
  "width": 128,
  "height": 32,
  "mapping": "TopDownAlternateRightLeft",
  "dotShape": "circle",
  "minDotSpacing": 2,
  "fillGapEnabled": false,
  "brightness": 1.0,
  "gamma": 0.8,
  "toneMapping": { ... },
  "temporalSmoothing": { ... },
  "visual": { ... },
  "bloom": { ... }
}
```

Core fields:

- `renderer`: `gpu` (default) or `cpu`.
- `width`, `height`: matrix resolution. Keep `width * height <= 8800` for safe DOF compatibility.
- `mapping`: logical index mapping mode (default: `TopDownAlternateRightLeft`).
- `dotShape`: `circle` or `square`.
- `minDotSpacing`: minimum spacing guardrail for renderer layout.
- `fillGapEnabled`: when `true`, renderer biases dot pitch toward the dominant viewport axis and uses stretch-to-fill behavior (short strips like `5x1`); when `false` (default), dot canvases stay square.
- `brightness`: normalized brightness scalar.
- `gamma`: gamma response control.

### `matrix.toneMapping`

- `enabled`
- `kneeStart`
- `strength`

### `matrix.temporalSmoothing`

- `enabled`
- `riseAlpha`
- `fallAlpha`

### `matrix.visual`

- `gpuPresentMode`: `DirectPresentOnly` (default) or `LegacyReadback`.
- `forceCpuDotRasterFallback`
- `enableDirectPresentParitySampling`
- `enableDiagnosticReadbackCapture`
- `flatShading`
- `fullBrightnessRadiusMinPct`
- `offStateTintR`, `offStateTintG`, `offStateTintB`
- `offStateAlpha`
- `lensFalloff`
- `specularHotspot`
- `rimHighlight`

### `matrix.bloom`

- `enabled`
- `threshold`
- `softKnee`
- `downsampleDivisor`
- `nearRadiusPx`
- `farRadiusPx`
- `nearStrength`
- `farStrength`

---

## `window`

- `alwaysOnTop`
- `borderless`
- `left`
- `top`
- `width`
- `height`

These values are persisted during runtime when the user moves/resizes the window.

---

## `debug`

- `showDebug`
- `logProtocol`
- `logFrames`

`logFrames` is only meaningful when protocol logging is enabled.

---

## `settings`

- `cabinetXmlPath`: path hint for `Cabinet.xml`.
- `cabinetToyName`: target LED toy name to update (default `Matrix1`).
- `autoUpdateCabinetOnResolutionChange`: if true, app attempts to update Cabinet.xml dimensions when resolution changes.
- `visualQuality`: settings-window quality preset label (`Low`, `Medium`, `High`, `Custom`).

---

## Notes on compatibility/defaulting

- The app applies legacy-safe defaults for older configs missing newer nested objects.
- Some legacy aliases are still accepted in deserialization-compatible paths (for older settings files).
- After startup, the app writes normalized settings back to disk, so unknown/invalid values may be corrected automatically.
