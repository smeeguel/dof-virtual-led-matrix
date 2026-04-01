# Config reference

## matrix.renderer
Legacy selector retained for compatibility.

- `gpu` (default)
- `cpu`
- `primitive`

## matrix.rendererOptions

```json
"rendererOptions": {
  "backend": "gpu",
  "allowCpuFallback": true,
  "enableGlow": false,
  "glowIntensity": 0.4
}
```

`backend` chooses preferred runtime backend; `allowCpuFallback` allows fallback if GPU init fails.
