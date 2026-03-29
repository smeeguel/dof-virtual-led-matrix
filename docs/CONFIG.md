# Virtual DOF Matrix Configuration

This project reads `settings.json` with camel-cased property names.

## Matrix visual shaping

`matrix.visual` now supports bulb-shaping controls that let you choose whether dots look like flat emitters or dome lenses.

```json
{
  "matrix": {
    "visual": {
      "shapeMode": "dome",
      "domeProfile": "diffused-dome",
      "edgeSoftness": 0.58
    }
  }
}
```

### `shapeMode`

- `dome` (default): enables radial dome attenuation/highlight behavior.
- `flat`: disables dome masking/specular shaping and renders a flatter LED style.

### `domeProfile`

- `smd-like`: tighter center energy and faster edge rolloff (PCB/SMD look).
- `diffused-dome` (default): balanced diffusion and lens depth.
- `strong-bulb`: brighter center + stronger specular response for a pronounced bulb look.

### `edgeSoftness`

Floating-point value from `0.05` to `1.0` (clamped):

- lower values = crisper edge transition
- higher values = softer edge transition

## Suggested visual presets

### SMD-like

```json
{
  "matrix": {
    "visual": {
      "shapeMode": "dome",
      "domeProfile": "smd-like",
      "edgeSoftness": 0.28
    }
  }
}
```

### Diffused dome

```json
{
  "matrix": {
    "visual": {
      "shapeMode": "dome",
      "domeProfile": "diffused-dome",
      "edgeSoftness": 0.58
    }
  }
}
```

### Strong bulb

```json
{
  "matrix": {
    "visual": {
      "shapeMode": "dome",
      "domeProfile": "strong-bulb",
      "edgeSoftness": 0.72
    }
  }
}
```
