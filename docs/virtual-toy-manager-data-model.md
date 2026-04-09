# Virtual Toy Manager Data Model and Compilation Rules

## Purpose

This document defines authoritative v1 data semantics so implementers can build the manager without guessing precedence, ownership, or compile behavior.

---

## Canonical entities

## 1) Global configuration

Global configuration stores default toy definitions that apply to all tables unless explicitly overridden.

Required properties:
- `schemaVersion` (integer)
- `virtualToys[]`
- `metadata` (generator/version/timestamp info)

## 2) Table configuration

Table configuration stores overrides keyed by table identity.

Required properties:
- `tables[]`
  - `tableId` (stable key)
  - `displayName`
  - `overrides[]`

## 3) Effective configuration view

At compile time, each table resolves to an effective configuration set produced by precedence rules below.

---

## Global vs table override precedence

## Precedence rule (authoritative)

For each configurable field:

1. If table override provides a non-null value, use table override value.
2. Else if global toy defines a value, use global value.
3. Else use system default (if the field has a default).
4. Else emit validation error (field required but unresolved).

## Deletion semantics

1. Deleting a global toy removes base definition for all tables.
2. Table overrides referencing deleted toy become invalid until remapped or removed.
3. Apply must be blocked while invalid references exist.

## Null handling

1. In override payloads, `null` means "inherit global" (not "set empty").
2. Empty string is treated as explicit value when field type is string.

---

## Sidecar JSON ownership rules

## Ownership boundary

1. **Manager-owned files** may be rewritten by the manager.
2. **Externally-owned files** are read-only inputs unless explicitly imported.

## Required manager-owned sidecar files

1. `virtual-toy-manager.json` (authoritative app-managed state for globals, table overrides, schema version, and write metadata)
2. `virtual-toy-manager.state.json` (optional UI/workflow state, not compile source)

## Write policy

1. Manager writes only manager-owned sidecar files plus generated output targets during Apply.
2. Manual edits to manager-owned files are allowed, but must pass schema validation on next load.
3. Manager must preserve unknown fields under `x-` extension namespaces when round-tripping JSON.

## Conflict policy

1. If generated output target has drifted from last manager snapshot and external edits are detected, show conflict warning before apply.
2. User can choose:
   - cancel and inspect diff
   - continue with overwrite (backup still required)

## Schema/version policy

1. All sidecar files require `schemaVersion`.
2. Loader must run migration pipeline from older known versions before edit/compile.
3. Unknown future version must fail fast with actionable error.

---

## Deterministic compile-to-XML requirement

## Determinism contract

Given identical validated sidecar JSON inputs and compile options, compiler must emit byte-identical XML output.

This includes:
1. Element ordering
2. Attribute ordering
3. Whitespace/newline policy
4. Numeric/text formatting
5. Stable identifier generation

## Canonical ordering rules

1. Sort toys by stable internal key (ordinal, case-insensitive).
2. Sort tables by `tableId` (ordinal, case-insensitive).
3. Within each table, sort overrides by toy key.
4. Preserve deterministic ordering for nested mapping arrays using explicit `order` field; if absent, order by key.

## Serialization rules

1. Newline style: `\n` only.
2. Indentation: 2 spaces.
3. No trailing whitespace.
4. UTF-8 without BOM unless consuming system strictly requires BOM.

## Validation before compile

Compile must fail if any condition is true:
1. Duplicate toy keys in global scope.
2. Duplicate table IDs.
3. Override references missing global toy key.
4. Any required effective field unresolved after precedence resolution.

## Output staging and atomicity

1. Compiler returns output in-memory.
2. Apply writes to temp file in same directory.
3. Apply atomically replaces target file.
4. On failure, previous file remains untouched or is restored from backup.

---

## Reference implementation expectations (v1)

1. Data model code should keep precedence logic separate from UI.
2. Compile logic should be testable as a pure function.
3. Sidecar read/write logic should be centralized to avoid schema drift.
4. At least one golden-file test must verify deterministic compile output.
