# Virtual DOF Matrix Emulator - Codex Brief

## Project goal

Build a **standalone, redistributable virtual LED matrix viewer/emulator** for the Visual Pinball / DOF ecosystem.

The emulator must:

- work **without modifying or forking DOF**
- work **without hardware**
- consume output from DOF through a **standard serial COM path**
- be suitable for sharing with the VPin community
- display matrix output in a **borderless, always-on-top, movable, resizable window**
- preserve very low overhead
- ideally persist position, size, scale, and display settings in a config file

At the current stage, the DOF handshake has been reverse-engineered enough to successfully receive matrix frame data from DOF through a VSPE virtual COM pair.

---

## Important constraint

This project must **not** require a custom DOF fork.

That means the solution must continue to impersonate a normal DOF-supported serial device, specifically the DOF `TeensyStripController` path.

Current working path:

`DOF -> COM1 -> VSPE virtual pair -> COM2 -> emulator app`

DOF believes it is talking to a Teensy strip controller.

---

## What is already proven

The following has already been successfully demonstrated:

1. `Cabinet.xml` loads correctly in DOF.
2. DOF loads `directoutputconfig30.ini` and resolves the correct ROM/table config.
3. DOF initializes `TeensyStripController` on a VSPE-created COM port.
4. A custom emulator on the paired COM port can:
   - answer the command-mode probe
   - answer setup commands
   - receive `R` strip-update packets
   - receive the final `O` output command
5. A full frame for a 32x8 matrix was captured successfully:
   - 256 LEDs
   - 768 RGB bytes

This means the transport and protocol are now working.

---

## Source-backed findings from DOF

### 1. Documented hardware-oriented limit

The uploaded `TeensyStripController.cs` documents the Teensy path as:

- **up to 8 WS2811/WS2812 strips**
- **up to 1100 LEDs per strip**

That implies a documented practical ceiling of:

- **8 x 1100 = 8800 LEDs/dots**

Source notes:

- The XML comment explicitly says the controller is used to control **up to 8** LED strips with **up to 1100 LEDs per strip**.
- The `M` setup command returns the controller's maximum LEDs per channel, and the controller rejects configurations above that value.

### 2. Minor implementation ambiguity

The class also contains:

- `protected int[] NumberOfLedsPerStrip = new int[10];`
- properties `NumberOfLedsStrip1` through `NumberOfLedsStrip10`

So the class surface exposes **10 strip properties**, but the controller documentation and OctoWS2811/Teensy comments still describe the intended hardware model as **8 strips**.

For this repo, treat the safe compatibility target as:

- **8 strips max**
- **1100 LEDs per strip max**
- **8800 LEDs total safe target**

Do **not** assume 10 strips are truly supported end-to-end without source verification in the update/write path.

### 3. Protocol summary from `TeensyStripController.cs`

The DOF controller behavior is now known.

#### Command-mode probe

DOF writes:

- `0x00`

Expected response:

- `'A'` (`0x41`) or `'N'` (`0x4E`)

If response is wrong, DOF may send a block of zero bytes and retry.

#### Setup sequence

After command mode succeeds, DOF uses:

1. `M`
   - emulator must reply with 3 bytes:
     - max LEDs high byte
     - max LEDs low byte
     - `'A'`

2. `L` + 2 bytes
   - sets LEDs per channel
   - emulator must reply with `'A'`

3. `C`
   - clear buffer
   - emulator must reply with `'A'`

4. `O`
   - output buffer
   - emulator must reply with `'A'`

#### Runtime frame update sequence

For strip data, DOF sends:

- `R`
- target position high byte
- target position low byte
- LED count high byte
- LED count low byte
- RGB payload of `led_count * 3` bytes

Then emulator must reply with:

- `'A'`

Then DOF sends:

- `O`

Then emulator must reply with:

- `'A'`

---

## Captured working example

A working captured update already exists and should be treated as canonical behavior for initial development:

- `L 01 00` => configured LEDs per channel = 256
- `R` packet target position = 0
- `R` packet LED count = 256
- RGB payload length = 768 bytes

This is exactly what we want for a 32x8 matrix.

---

## Recommended implementation language and stack

### Recommendation: switch from Python prototype to C#/.NET 8

Reasoning:

- DOF itself is C#/.NET, so protocol matching and serial behavior are easier to reason about in the same ecosystem.
- Windows desktop packaging is easier for community redistribution.
- A single-file Windows build is realistic.
- Always-on-top, borderless, movable, resizable window behavior is straightforward in WPF.
- Serial COM handling is solid via `System.IO.Ports`.

### Preferred app architecture

Use **C# + .NET 8 + WPF**.

This project is Windows-specific anyway because DOF and VPX are Windows-oriented.

### Suggested components

- **Core library**
  - protocol parser/state machine
  - frame buffer model
  - settings model
- **Desktop app**
  - COM listener/emulator
  - matrix renderer window
  - basic settings UI if desired
- **Optional CLI mode**
  - headless debug mode
  - dumps frames to file/log for troubleshooting

### Suggested NuGet packages

Try to keep dependencies minimal.

Possibly:

- built-in `System.IO.Ports`
- built-in WPF rendering first
- optional JSON config via `System.Text.Json`
- avoid heavy UI frameworks unless necessary

No external graphics engine is needed for the first shareable version.

---

## Recommended product structure

### App modes

The desktop app should eventually support two modes:

#### 1. Emulator-only mode

- opens the configured COM port
- impersonates the Teensy controller
- logs protocol traffic
- receives frames

#### 2. Emulator + viewer mode

- does all of the above
- also opens a borderless display window
- renders the latest matrix frame live

---

## MVP requirements

### Functional

- choose COM port from config file and optionally a small UI
- emulate the Teensy serial protocol used by DOF
- parse `M`, `L`, `C`, `R`, and `O`
- maintain one or more frame buffers
- render a matrix window from RGB payload data

### Window behavior

- borderless
- always on top
- resizable
- movable by dragging
- optional click-through mode later
- remember last size and position

### Rendering behavior

- nearest-neighbor scaling option
- soft glow/bloom simulation option
- square dot mode and circular dot mode
- brightness multiplier / gamma option
- black background

### Config persistence

Store settings in JSON, for example:

- selected COM port
- baud rate
- matrix width
- matrix height
- dot shape
- pixel spacing
- brightness
- always-on-top
- window position and size
- arrangement/mapping mode

---

## Matrix geometry and mapping

The current DOF cabinet setup uses:

- width = 32
- height = 8
- arrangement = `TopDownAlternateRightLeft`

That means the emulator must support serpentine mapping.

### Initial mapping requirement

Implement at least:

- row-major
- column-major
- serpentine alternate direction

Specifically include support for:

- `TopDownAlternateRightLeft`

The parser should keep the raw RGB order from DOF and map into display coordinates separately.

---

## Maximum supported size target for this repo

### Safe DOF-compatible target

Treat these as the compatibility target:

- **8 strips max**
- **1100 LEDs per strip max**
- **8800 LEDs total**

### For the viewer app

The viewer should not hardcode 32x8.

It should support arbitrary matrix sizes that fit within the safe DOF-compatible total.

Examples:

- 32x8 = 256
- 64x32 = 2048
- 128x32 = 4096
- 128x64 = 8192

### Important caveat

A very high resolution matrix may still depend on how `Cabinet.xml` is arranged and how many strips are declared in DOF. The viewer can support high totals, but the COM emulator must remain compatible with how DOF chunks strip updates.

So the viewer should be generic, but the serial emulation must remain faithful to strip-based updates.

---

## Key technical direction

### Protocol layer

Implement a deterministic state machine, not ad hoc byte guessing.

Suggested states:

- waiting for command mode probe
- ready
- waiting for `L` payload
- waiting for `R` header
- waiting for `R` RGB payload

The prototype already proved that buffering matters. Data can arrive split across reads or combined into one read. Therefore:

- maintain a single RX buffer
- parse commands from the buffer
- never assume each serial read corresponds to one protocol packet

### Frame model

Maintain:

- one raw LED buffer per strip if needed
- one flattened logical matrix buffer for rendering

For the MVP, one strip is enough, but architecture should not assume one strip forever.

---

## Suggested repository layout

```text
virtual-dof-matrix/
  README.md
  docs/
    CODEX_BRIEF.md
    PROTOCOL.md
    ARCHITECTURE.md
    CONFIG.md
  src/
    VirtualDofMatrix.App/
    VirtualDofMatrix.Core/
    VirtualDofMatrix.Tests/
  examples/
    Cabinet.xml
    settings.sample.json
  tools/
    protocol-capture-notes.txt
```

---

## Suggested milestones

### Milestone 1 - solid emulator core

- C# console app
- open COM port
- implement protocol state machine
- log traffic
- acknowledge all required DOF commands correctly
- confirm identical working behavior to the current Python prototype

### Milestone 2 - viewer window

- WPF borderless always-on-top window
- render 32x8 test matrix from received frames
- add scale and position persistence

### Milestone 3 - generic matrix sizing

- configurable width/height
- configurable mapping modes
- support higher resolutions up to safe total limit

### Milestone 4 - polish

- simple settings window
- tray icon
- startup options
- optional click-through / transparency features

### Milestone 5 - packaging

- publish self-contained Windows build
- document VSPE setup
- provide example `Cabinet.xml`

---

## Explicit non-goals for now

- no DOF fork
- no hardware dependency
- no Unreal integration in the first shipping version
- no paid dependencies required for core function
- no requirement to support every theoretical DOF controller type

Focus on **TeensyStripController emulation first**.

---

## Important current dependency

At the moment, the working test setup uses **VSPE** to create the virtual COM pair.

Assume current dev environment:

- DOF side uses `COM1`
- emulator side uses `COM2`

Do not remove this assumption for the initial implementation, but make COM ports configurable.

---

## Example config structure

```json
{
  "serial": {
    "portName": "COM2",
    "baudRate": 9600,
    "maxLedsPerChannel": 1100
  },
  "matrix": {
    "width": 32,
    "height": 8,
    "mapping": "TopDownAlternateRightLeft",
    "dotShape": "circle",
    "dotSize": 18,
    "dotSpacing": 4,
    "brightness": 1.0,
    "gamma": 1.0
  },
  "window": {
    "alwaysOnTop": true,
    "borderless": true,
    "left": 100,
    "top": 100,
    "width": 1280,
    "height": 320
  },
  "debug": {
    "logProtocol": true,
    "logFrames": false
  }
}
```

---

## Immediate first tasks for Codex

1. Create a .NET 8 solution.
2. Build a console-based serial protocol emulator that matches the current Python behavior.
3. Reproduce these responses exactly:
   - `0x00` -> `A`
   - `M` -> `max_hi max_lo A`
   - `L + 2 bytes` -> `A`
   - `C` -> `A`
   - `O` -> `A`
   - `R + header + rgb payload` -> `A`
4. Store the most recent RGB payload as the current frame.
5. Add a WPF window that visualizes the frame.
6. Keep logs very explicit and readable.

---

## Testing expectations

### Minimum test cases

- command-mode success
- `M` response correctness
- `L` parse correctness
- `R` packet parse correctness for 256 LEDs
- combined-read parsing, where command + payload arrive in one read
- split-read parsing, where payload arrives over multiple reads
- viewer updates when a new frame arrives

### Known-good observed runtime sequence

A known-good observed sequence already exists:

- `00`
- `M`
- `L 01 00`
- `C`
- `O`
- `R + 4-byte header + 768-byte payload`
- `O`

The implementation should be tested against this behavior.

---

## Notes for future Unreal integration

Not for MVP, but keep the frame model clean enough that later it can:

- stream raw RGB frame data to another process
- optionally expose shared memory or pipe output later
- optionally output image frames for a UE texture pipeline later

Do not couple the serial parser directly to WPF drawing code.

---

## Bottom line

The hardest reverse-engineering work is already done.

The current repository should now focus on turning a proven Python serial prototype into a clean, shareable Windows application:

- **C#/.NET 8**
- **WPF**
- **faithful TeensyStripController serial emulation**
- **lightweight always-on-top virtual matrix viewer**

