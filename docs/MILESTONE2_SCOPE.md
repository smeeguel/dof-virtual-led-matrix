# Milestone 2 Scope (Viewer Bootstrap)

## Purpose

Milestone 2 focuses on a **known-good 32x8 path first**:

- Keep the serial protocol emulation stable and DOF-compatible.
- Present frames in a WPF viewer window with low complexity.
- Keep mapping and rendering interfaces ready for expansion, without claiming full generalized geometry support yet.

This is intentionally narrower than Milestone 3.

---

## In-scope for Milestone 2

1. Protocol ingestion and frame presentation for the known-good sequence (`00`, `M`, `L`, `C`, `O`, `R`, `O`).
2. Snapshot-based frame handoff at `O` output boundaries.
3. Rendering of 32x8 matrix payloads in app layer via pluggable renderer interface.
4. Configuration-driven defaults aligned with current test setup (`COM2`, `9600`, `32x8`, `TopDownAlternateRightLeft`).

---

## Exact index-to-coordinate mapping for MS2 demo

MS2 demo behavior uses `TopDownAlternateRightLeft` by default.

Given:

- matrix width = `W`
- matrix height = `H`
- zero-based linear LED index = `i`

Mapping used:

1. `columnFromRight = i / H`
2. `yInColumn = i % H`
3. `x = (W - 1) - columnFromRight`
4. If `columnFromRight` is even, `y = yInColumn`; otherwise `y = (H - 1) - yInColumn`

For the known-good `32x8` setup:

- `i = 0` maps to `(31, 0)` (top-right)
- `i = 7` maps to `(31, 7)`
- `i = 8` maps to `(30, 7)`

This is the behavior currently expected by out-of-box testing.

---

## Deferred mapping APIs for Milestone 3

The architecture has hooks for broader mapping, but full geometry generalization is deferred.

Milestone 3 will formalize and validate:

- `row-major`
- `column-major`
- `TopDownAlternateRightLeft` for arbitrary matrix sizes and full rendering validation

Milestone 2 should be treated as "32x8 + proven mapping path" rather than complete mapping coverage.

---

## Key implementation locations

Core protocol and frame state:

- `src/VirtualDofMatrix.Core/FrameBuffer.cs`
- `src/VirtualDofMatrix.Core/TeensyProtocolEngine.cs`

App presentation and rendering path:

- `src/VirtualDofMatrix.App/Rendering/IMatrixRenderer.cs`
- `src/VirtualDofMatrix.App/Rendering/WpfPrimitiveMatrixRenderer.cs`
- `src/VirtualDofMatrix.App/MainWindow.xaml.cs`

These locations define the current Milestone 2 boundaries and extension points.
