# Virtual Toy Manager UX Specification

## UX principles (v1)

1. **Predictable over powerful**: never hide precedence outcomes.
2. **Novice-safe defaults**: prefill values and explain consequences.
3. **No silent writes**: users must explicitly Apply before file changes happen.
4. **Progressive disclosure**: advanced fields are tucked behind "Advanced" sections.

---

## Virtual toy row interaction model (implemented)

The Virtual Toys list now uses a two-layer interaction model so users can preview quickly without losing an explicit selection target.

### Hover preview (transient default)

1. Moving the pointer over a toy row applies a subtle hover tint in the list.
2. Hover emits a preview event to the window adapter.
3. Leaving the row clears preview automatically.
4. Hover is transient and never changes the locked toy selection.

### Click lock (explicit selection)

1. Clicking a row locks that toy as the active selection.
2. Locked rows use a stronger tint than hover.
3. Locked selection controls focused window/border targeting.
4. Clicking a different row moves the lock to that row.

### Unselect lifecycle

1. Clicking the already selected row again clears the lock.
2. Clearing lock emits a selection-clear event (`null` toy id semantics).
3. Hover can continue to preview toys when no lock exists.
4. If a locked toy is deleted/rebuilt out of the current list, lock state is cleared.

---

## Information architecture (IA)

## 1) Dashboard

**Primary jobs**
- Tell user whether configuration is healthy.
- Show whether unapplied changes exist.
- Provide direct route to fix errors.

**Required sections**
1. **Status card**
   - `Healthy`, `Warnings`, or `Blocked` state.
2. **Last apply card**
   - Timestamp, result, and files changed count.
3. **Pending changes card**
   - Count of added/edited/deleted entities not yet applied.
4. **Quick actions**
   - `Add Virtual Toy`, `Add Table Override`, `Go to Apply`.

## 2) Virtual Toys

**Primary jobs**
- Manage global toy definitions.

**Required elements**
1. Toy list table (name, kind, enabled, last edited).
2. Search/filter by kind and enabled state.
3. Row actions: `Edit`, `Duplicate`, `Delete`.
4. Primary CTA: `Add Virtual Toy`.

## 3) Tables

**Primary jobs**
- Manage per-table overrides.

**Required elements**
1. Table list (table id/name, override count, health state).
2. Row action: `Edit Overrides`.
3. Effective-value inspector (shows final value and source).
4. Primary CTA: `Add Table`.

## 4) Apply

**Primary jobs**
- Confirm and execute write operations safely.

**Required elements**
1. Pending change summary grouped by entity type.
2. Validation panel with errors/warnings.
3. Files-to-be-written list.
4. `Apply Changes` CTA (disabled on validation errors).
5. Post-apply result panel with backup id and rollback shortcut.

---

## Add/Edit wizard flow

The same wizard shell is used for both **Virtual Toy** and **Table Override** editing.

## Step model

### Step 0: Entry context
- Entry points:
  - Add toy
  - Edit toy
  - Add table override
  - Edit table override
- Wizard title must reflect intent (e.g., `Add Matrix Toy`, `Edit Flasher Override`).

### Step 1: Basic identity
- Fields:
  - Display name (required)
  - Internal id/key (auto-generated, editable with validation)
  - Kind selector (matrix/topper/flasher)
- Validation:
  - Key uniqueness within scope.

### Step 2: Behavior and mapping
- Fields vary by kind, but must include:
  - Enabled toggle
  - Source mapping range
  - Geometry/render fields where relevant
- UX requirement:
  - Inline examples for valid value formats.

### Step 3: Scope and precedence
- For globals:
  - Show "applies to all tables unless overridden" note.
- For table overrides:
  - Select target table.
  - Show which global values are being overridden.

### Step 4: Review
- Show changed fields only (before/after).
- Show computed effective result preview.
- Show potential warnings.

### Step 5: Save (staged)
- Save updates manager model only (no generated file writes yet).
- Route user to Apply page if this creates pending changes.

## Wizard guardrails

1. Prevent finishing with unresolved required fields.
2. Block destructive delete when active table overrides depend on item, unless dependency is explicitly resolved.
3. On cancel with unsaved edits, require confirmation.

---

## Novice copy guidelines

## Tone and language

1. Use plain language, active voice.
2. Avoid internal jargon unless immediately defined.
3. Keep sentence length short (target < 20 words).

## Required message format for errors

Every blocking error must include:

1. **What happened**
2. **Why it matters**
3. **How to fix it now**

Example template:

- **We couldn't apply your changes.**
- `Matrix width` is missing for `backglass-main`, so output would be incomplete.
- Open that toy, set width and height, then try Apply again.

## Labels and actions

1. Prefer explicit verbs: `Add Virtual Toy`, `Apply Changes`, `Restore Backup`.
2. Avoid ambiguous labels like `Submit` or `Process`.
3. Confirm destructive actions with object name in prompt.

## Empty states

1. Explain current state in one sentence.
2. Provide exactly one recommended CTA.
3. Include short "what happens next" helper text.

## Warning copy

1. Warnings should not sound fatal.
2. Explain impact if user proceeds.
3. If safe to continue, provide `Apply anyway` path with explicit acknowledgement.

---

## Accessibility and usability minimums (v1)

1. Keyboard accessible navigation for all primary workflows.
2. Visible focus indicators on interactive controls.
3. Color is never the only signal for errors/warnings.
4. Error summaries include links/focus jump to invalid fields.
