# Virtual Toy Manager MVP Specification

## Purpose

This document defines the **v1 product scope** for a Virtual Toy Manager that edits and validates virtual toy configuration for the Virtual DOF Matrix app without requiring manual INI surgery.

The goal is to let users confidently manage global defaults and per-table overrides while preserving deterministic output to DOF-facing config artifacts.

---

## Product goals

1. **Safe editing for non-experts**
   - Users can add/edit/remove virtual toys through guided UI, not raw file editing.
2. **Deterministic output**
   - Equivalent input state always compiles to byte-identical generated XML payload (ordering, formatting, and values).
3. **Clear precedence model**
   - Global defaults and table overrides are visible and predictable.
4. **Rollback-first operations**
   - Every apply operation creates recoverable snapshots before writing generated files.
5. **No DOF fork / no runtime protocol changes**
   - Tool only manages config data consumed by current emulator path.

---

## Finalized v1 scope

### In scope (must ship in v1)

1. **Dashboard page**
   - Shows configuration health, last apply timestamp, last apply result, and active profile path.
2. **Virtual Toys page**
   - List/create/edit/delete toys.
   - Supported toy kinds in v1: `matrix`, `topper`, `flasher`.
3. **Tables page**
   - List known tables.
   - Create/edit table-level overrides that can override global toy settings.
4. **Apply page**
   - Preview of pending changes.
   - Deterministic compile step to generated XML output.
   - Validation summary (errors block apply, warnings allow apply with confirmation).
5. **Add/Edit wizard**
   - Guided flow for creating or editing a toy or table override.
6. **Sidecar JSON storage**
   - Persist manager-owned state in sidecar JSON files (see data model doc).
7. **Backup + rollback**
   - Before apply, create timestamped backup of generated targets.
   - One-click rollback to any backup created by the manager.
8. **Audit log (local)**
   - Append-only local log entry for apply/rollback (timestamp, user action, file set, result).

### Explicit non-goals (must not be included in v1)

1. Remote/cloud sync.
2. Multi-user conflict resolution.
3. Live DOF process restart/interop controls.
4. Auto-discovery and mutation of arbitrary third-party config formats.
5. Visual canvas layout editor with drag-and-drop toy placement.
6. Full localization system (English-only copy in v1).

---

## Milestone plan with acceptance criteria

## Milestone 1: Data and compile core

**Deliverables**
- In-memory model for globals, tables, overrides.
- Deterministic compile engine.
- Schema validation for sidecar JSON.

**Acceptance criteria**
1. Given unchanged sidecar JSON, two consecutive compile runs produce byte-identical XML output.
2. Invalid model state returns structured validation errors with exact field paths.
3. Compile engine is pure (no direct file writes) and returns output payload + diagnostics.

## Milestone 2: CRUD UI (Dashboard / Virtual Toys / Tables)

**Deliverables**
- IA skeleton pages.
- Create/edit/delete flows for toys and table overrides.
- Read-only diagnostics panel.

**Acceptance criteria**
1. User can create a toy end-to-end without editing files manually.
2. User can define table override for at least one table and see effective-value preview.
3. Deleting a toy updates references and blocks apply if unresolved references remain.

## Milestone 3: Apply + backups + rollback

**Deliverables**
- Apply pipeline with preflight validation.
- Backup creation.
- Rollback command and history UI.

**Acceptance criteria**
1. Apply writes generated output only after preflight validation passes.
2. Every apply produces a restorable backup artifact.
3. Rollback restores exact previous generated output and records audit entry.

## Milestone 4: UX hardening for novice users

**Deliverables**
- Inline help copy.
- Wizard guardrails.
- Warning/error phrasing improvements.

**Acceptance criteria**
1. Novice user can complete "add matrix toy and apply" without external documentation.
2. All blocking errors include "what happened", "why", and "how to fix" text.
3. Apply page clearly states what files will be changed before commit.

---

## Risks and rollback strategy

## Primary risks

1. **Precedence confusion**
   - Risk: users cannot predict whether global or table value is active.
   - Mitigation: always show effective values with provenance labels (`global`, `table override`).
2. **Non-deterministic output drift**
   - Risk: generated XML churn causes noisy diffs and hard debugging.
   - Mitigation: strict canonical ordering + formatting rules; deterministic serializer tests.
3. **Sidecar/manual edit conflicts**
   - Risk: manual edits to manager-owned output are overwritten unexpectedly.
   - Mitigation: ownership markers + pre-apply conflict warning + backup-first workflow.
4. **Partial apply failures**
   - Risk: some files updated, others not, leaving broken config state.
   - Mitigation: staged temp write + atomic replace; if any write fails, restore from latest backup.
5. **Schema evolution breakage**
   - Risk: future model changes break older sidecar files.
   - Mitigation: explicit schema version and migration step before load.

## Rollback strategy

1. **Snapshot timing**
   - Create backup snapshot immediately before any generated target is touched.
2. **Snapshot contents**
   - Include every generated file the apply operation may modify.
3. **Restore behavior**
   - Rollback restores selected snapshot across all managed generated files in one operation.
4. **Verification**
   - Post-restore verification hashes restored files and compares with snapshot manifest.
5. **Operator feedback**
   - UI reports rollback success/failure per file and persists an audit record.

---

## Definition of done for v1

v1 is complete only when:

1. All milestone acceptance criteria pass.
2. Deterministic compile tests are green.
3. Apply and rollback have been validated on at least one representative DOF setup.
4. A new user can configure one matrix toy and one table override from the UI with no manual file edits.
