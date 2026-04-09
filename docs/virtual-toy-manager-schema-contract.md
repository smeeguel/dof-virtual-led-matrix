# Virtual Toy Manager Sidecar Contract (`virtual-toy-manager.json`)

This document is the implementation guide for the sidecar state file validated by `docs/virtual-toy-manager.schema.json`.

---

## File purpose and ownership

- File name: `virtual-toy-manager.json`
- Ownership: app-managed (the manager can rewrite this file).
- Audience: runtime loader, editor UI, and compile pipeline.

The file is the single source of truth for editable manager state:

1. global virtual toys,
2. optional per-table overrides,
3. schema version,
4. last write metadata.

---

## Top-level structure

```json
{
  "schemaVersion": 1,
  "lastWrite": {},
  "globalVirtualToys": [],
  "tableOverrides": []
}
```

### Required fields

1. `schemaVersion` (integer, currently `1`)
2. `lastWrite` (object)
3. `globalVirtualToys` (array)
4. `tableOverrides` (array)

---

## Field contracts

## `schemaVersion`

- Type: integer
- Current value: `1`
- Loader rule:
  - If `schemaVersion == 1`, load with v1 parser.
  - If lower than current known version, run migration pipeline.
  - If greater than current known version, fail fast with explicit unsupported-version error.

## `lastWrite`

Tracks write provenance for diagnostics and support.

Required fields:

- `timestampUtc` (ISO-8601 UTC string)
- `writer` (app/process identity)
- `appVersion` (version string)

Optional fields:

- `sourceCommit`
- `machineName`

## `globalVirtualToys`

Array of global toy definitions used as base defaults for every table.

Each toy requires:

- `toyKey` (stable id, regex `^[a-z0-9-]{1,64}$`)
- `kind` (`matrix | topper | flasher`)
- `enabled` (boolean)
- `source` (`startLed`, `ledCount`)
- `render` (`brightness`, `gamma`, optional `dotShape`)
- `model` (variant payload; see below)

### Validation constraints

1. `toyKey` must be unique across `globalVirtualToys`.
2. `source.startLed` must be `0..8799`.
3. `source.ledCount` must be `1..8800`.
4. Runtime rule: `source.startLed + source.ledCount <= 8800`.
5. `brightness` range: `0.0..4.0`.
6. `gamma` range: `0.1..4.0`.

## `tableOverrides`

Array of per-table override sets.

Each set requires:

- `tableId` (stable identity key)
- `enabled` (opt-in switch)
- `overrides` (array)

Optional:

- `displayName`

### Opt-in behavior

- `enabled: false` means ignore the set entirely during compile.
- `enabled: true` means merge `overrides` onto global toy state for that table.

### Override field semantics

- `toyKey` identifies target global toy.
- Nullable fields (`enabled`, `brightness`, `gamma`, `mappingMode`):
  - `null` => inherit global value
  - non-null => override global value for that table only

---

## Variant models (required)

## 1) Single bulb variant

```json
{
  "variant": "single-bulb",
  "mappingMode": "RowMajorLeftToRight"
}
```

Rules:

1. Represents exactly one logical output element.
2. Supported mapping mode in v1: `RowMajorLeftToRight`.

## 2) Row variant

```json
{
  "variant": "row",
  "length": 32,
  "mappingMode": "RowMajorLeftToRight"
}
```

Rules:

1. `length` range: `2..8800`.
2. Supported mapping mode in v1: `RowMajorLeftToRight`.

## 3) 2D grid/matrix variant

```json
{
  "variant": "matrix-2d",
  "width": 32,
  "height": 8,
  "mappingMode": "TopDownAlternateRightLeft"
}
```

Rules:

1. `width` and `height` range: `2..1100`.
2. Runtime rule: `width * height <= 8800`.
3. Allowed mapping modes:
   - `RowMajorLeftToRight`
   - `ColumnMajorTopToBottom`
   - `TopDownAlternateRightLeft`

---

## Mapping mode enums (v1 contract)

1. `RowMajorLeftToRight`
2. `ColumnMajorTopToBottom`
3. `TopDownAlternateRightLeft`

Notes:

- Single bulb and row variants intentionally restrict to `RowMajorLeftToRight` in v1.
- Matrix variant supports the full enum set above.

---

## Precedence and compile behavior

Effective value resolution per field:

1. table override value (if non-null), else
2. global value, else
3. default (if field defines one), else
4. validation error.

Compile must be deterministic for identical validated input:

- stable ordering by keys/ids,
- stable formatting,
- stable newline policy.

---

## Migration behavior (forward/backward compatibility)

## Backward compatibility (older files)

When loading `schemaVersion < current`:

1. Run sequential migrators (`v1->v2->...`) until current version is reached.
2. Migrators must be pure transforms over JSON payload.
3. On success, persist upgraded payload with updated `schemaVersion` and `lastWrite`.

## Forward compatibility (newer files)

When loading `schemaVersion > current`:

1. Do not attempt partial load.
2. Fail with `UnsupportedSchemaVersion` error.
3. Error message must include:
   - file version,
   - max supported version,
   - remediation (`upgrade app to newer version`).

## Unknown field policy

1. Reject unknown top-level fields (schema is strict).
2. Preserve future extension fields only under explicit extension namespaces when introduced in a future version.
3. Do not silently drop unknown required semantics.

## Migration safety requirements

1. Create backup before writing migrated output.
2. Log migration steps with from/to versions.
3. If migration fails, keep original file untouched and report actionable diagnostics.

---

## Implementation checklist

1. Validate against `docs/virtual-toy-manager.schema.json` before save and before compile.
2. Enforce runtime constraints not expressible by JSON Schema (`width * height <= 8800`, source end bound).
3. Keep migration pipeline unit-tested with fixture files for each supported version.
