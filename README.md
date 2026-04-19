# Virtual DOF Matrix by Migs: Setup Guide

This guide is written for everyday cabinet owners, including first-time DOF users.

GitHub repo:
https://github.com/smeeguel/dof-virtual-led-matrix

Need help or want to share feedback? Use this simple form:
https://forms.gle/1CWDqyuZR86VikSF6

Prefer a visual walkthrough? Watch the beginner setup guide below:

[![Watch the Virtual DOF Matrix setup guide](https://img.youtube.com/vi/T-KVuO11OeE/hqdefault.jpg)](https://www.youtube.com/watch?v=T-KVuO11OeE)

---

## What this app does

Virtual DOF Matrix displays DOF LED output in one or more virtual on-screen windows, such as a backglass matrix or addressable LED strips.

You do not need to understand DOF deeply to use this app. Just follow the steps in order.

---

## Quick Start (Recommended)

1. Install DOF from mjrnet (Step 1A)
2. Run `DOFConfigSetup.bat` from this package (Step 1B)
3. Launch `VirtualDofMatrix.App.exe`
4. Launch a VPX table

If it does not work, come back to the troubleshooting section in Step 11.

---

## Before you start

You need:

- Windows
- This Virtual DOF Matrix package ZIP
- Visual Pinball X (VPX) if you want to launch tables
- A front-end like PinUP Popper only if you use a front-end workflow (optional)

If DOF is not installed yet, Step 1 covers that.

---

## 1) Install DOF first, then run the setup script

This order matters.

### Step 1A - Install DOF from mjrnet

Download DOF from:

- http://mjrnet.org/pinscape/dll-updates.html#DOF

That page includes full DOF installers for both:

- x64
- x86

Install DOF to:

- `C:\DirectOutput`

If you already have DOF installed, make a backup of the entire folder before continuing.

Example:

- `C:\DOFBackup`

After install, you should have these folders:

- `C:\DirectOutput\x64`
- `C:\DirectOutput\x86`

### Step 1B - Run the package setup script

From the package root:

1. Run `DOFConfigSetup.bat`
2. Pick the template that matches your setup
3. Confirm the copy when prompted

What the script does:

- Copies the correct config files into your DOF folder
- Updates the DOF DLL files
- Creates a backup when needed

You do not need to copy files manually.

Quick troubleshooting:

- If `C:\DirectOutput\Config` is not found, continue in the script and select the correct folder manually
- If copy fails, run `DOFConfigSetup.bat` as Administrator and try again
- If Windows blocks the script, right-click `DOFConfigSetup.bat` and choose **Run anyway**

✅ At this point, DOF should be installed and configured for the app.

---

## 2) Optional: Cabinet.xml for combination setups (virtual + hardware)

⚠️ Most users can skip this section.

Use Step 2 only if you have a combination setup with virtual + hardware toys, custom routing, or older custom DOF files you need to keep.

Before editing, make a backup copy of `Cabinet.xml`.

### Step 2A - Open Cabinet.xml

1. Go to `C:\DirectOutput\Config`
2. Open `Cabinet.xml` in a text editor such as Notepad++

### Step 2B - Find the virtual matrix controller entry

Search for:

- `VirtualLEDStripController`

If Step 1B completed correctly, this entry should already be present.

### Step 2C - Confirm the pipe/controller name matches app settings

In `Cabinet.xml`, find the controller pipe name.

In `settings.json`, find:

```json
"transport": {
  "pipeName": "VirtualDofMatrix"
}
```

These names must match exactly.

### Step 2D - Let the app update Cabinet.xml when needed

When **Auto-update Cabinet on resolution change** is enabled, the app:

1. Shows a summary of the changes it wants to make
2. Updates `Cabinet.xml` only after you confirm

The app updates only its managed virtual toy data and leaves unrelated hardware sections alone.

---

## 3) First launch order

Always start in this order:

1. Launch `VirtualDofMatrix.App.exe`
2. Launch Popper or a VPX table

If VPX or DOF starts first, the app might miss the initial connection.

If you use the Popper startup automation in Step 10, Popper can launch the app for you.

---

## 4) Toy setup workflow (recommended)

For normal use, manage toys in:

- **Settings -> Virtual Toys**

From there you can:

- Add toys
- Edit toys
- Enable or disable toys
- Move and resize toy windows
- Click **OK** to save changes (global + active table overrides).

If you are new, this is the recommended workflow.

---

## 5) Add a new toy

Use this when creating a second, third, or fourth virtual output.

### Step 5A - Open Settings

1. Launch the app
2. Open the **Settings** window
3. Click the **Virtual Toys** tab

### Step 5B - Start the Add Toy flow

1. Click **Add Toy**
2. Choose a toy type:
   - **Single strip**
   - **Full matrix**
3. Enter a toy name you will recognize later, such as `Strip2` or `Matrix2`

### Step 5C - Set geometry and mapping

For **Full matrix**, set:

- `width`
- `height`
- `mapping`

For a standard 32x8 DMD-style matrix:

- Width = `32`
- Height = `8`
- Mapping = `TopDownAlternateRightLeft`

For **Single strip**, set:

- `bulb count`
- `direction` (`Horizontal` or `Vertical`)

### Step 5D - Optional visual settings

You can keep the defaults, or adjust options such as:

- Dot shape
- Spacing
- Brightness
- Background color
- Window behavior
- Glow

### Step 5E - Save and test

1. Make sure the toy is enabled
2. Click **Save**
3. Confirm the toy appears in the list
4. Click **OK**
5. Launch a table and confirm the toy animates

---

## 6) Edit an existing toy

Use this when you want to adjust a toy you already created.

### Step 6A - Select the toy

1. Open **Settings -> Virtual Toys**
2. Click the toy row you want to edit

### Step 6B - Make your changes

Common edits:

- Enable or disable the toy
- Change strip or matrix dimensions
- Change window position or size
- Change background color
- Toggle lock aspect ratio

### Step 6C - Save your changes

1. Click **OK**
2. If prompted for cabinet updates, review the summary and confirm
3. Make sure the toy window refreshes as expected

---

## 7) Virtual Toys behavior notes

In **Settings -> Virtual Toys**:

- Each toy row has an on/off switch
- Turning a toy off hides its viewer window
- At least one toy must stay enabled
- If a table is running, toy enable/disable can be table-specific (active table scope) without changing your global default
- You can also right-click a toy window and choose **Disable for &lt;active table&gt;** for a quick table-specific disable
- If you change toy width or height and save, the window rebuilds immediately
- New toys (and Cabinet.xml bootstrap toys) that do not already have both window position values are auto-placed on first spawn so their windows do not overlap by default
- You can keep Settings open while moving or resizing toy windows
- Window move/resize now saves automatically:
  - No active table: saves to **global** toy window position/size
  - Active table: saves to that table's **table-specific** window position/size override
- While a table is active, you can right-click a toy window and choose **Save &lt;toyname&gt; global position** to copy the current geometry to global and clear that toy's table-specific geometry override
- Choosing **Exit** from any viewer window closes the full app

---


## Table-specific visibility override file

Per-table toy overrides are saved to a dedicated INI file (not the app install folder):

- Default path: `<app-folder>\table-toy-overrides.ini`
- Optional override: `routing.tableOverrideIniPath` in `settings.json`

Format (currently used keys):

```ini
[table:Tron Legacy (Stern 2011)]
toy:matrix-main.enabled = true
toy:matrix-topper.enabled = false
toy:matrix-main.window.left = 100
toy:matrix-main.window.top = 200
toy:matrix-main.window.width = 960
toy:matrix-main.window.height = 240
```

Both `enabled` and `window.*` are active now:

- `enabled` controls table-specific on/off behavior
- `window.left/top/width/height` controls table-specific window geometry

Most users do not need to edit this file manually. It is updated automatically by the GUI when you toggle toy scope visibility or move/resize toy windows during an active table.

Tip: if you want to keep the current table layout but make it your global default, right-click that toy window and choose **Save &lt;toyname&gt; global position**.

Keep toy IDs exactly the same as your configured routing toy IDs.


## 8) DOF-side changes needed for extra toys

Adding toys in the app is the correct first step, but DOF must still output data for those toy ranges.

In general, these need to stay aligned:

1. `Cabinet.xml` toy/controller definitions
2. DOF Config Tool assignments
3. Re-generated DOF config files copied back to your cabinet

---

## 9) Add custom GIF / community light shows (DOF Config Tool)

This step is optional, but highly recommended if you want enhanced light shows, including community-created GIF animations.

Open the DOF Config Tool:

- https://configtool.vpuniverse.com/app/home

### Important note

The pre-packaged configs included with this app **do NOT include custom GIF animations**.

- These animations are created by the community
- They are not mine to redistribute
- You can download them directly from the DOF Config Tool, one table at a time

### Step 9A - Create or import a cabinet

1. Go to **Cabinet -> Manage**
2. Create a cabinet
3. Click the **three dots** next to your cabinet
4. Choose **Import Cabinet...**

From your Virtual DOF Matrix download, open:

```
DOF\ConfigToolCabinets
```

Choose one of these cabinet templates:

- **Single matrix only**
  - `Cabinet_SingleMatrix.json`
- **Matrix + 3 LED strips**
  - `Cabinet_BackMatrixPlus4LEDStrips.json`

### What this import adds

Importing the cabinet JSON sets up the DOF-side pieces this app needs, including:

- 1 **Teensy device** (emulated by the client app)
- One or more **Toy Combos**
- One or more **Port Assignments**

Those port assignments tell DOF which devices to output to, and in which LED order.

### Step 9B - Add custom table effects and GIF animations

Once the cabinet JSON is imported, you can add custom table effects.

1. Go to **Tables -> Configurations**
2. Search for the table you want to customize
3. Open that table's configuration

If the table supports enhanced effects, you will see an expandable section called:

- **Special MX Effect Configurations (optional)**

To enable them:

1. Expand that section
2. Download any embedded GIF(s) you want
3. Click **Apply MX Effects to your User Configuration**
4. Click **Save / Update**

### Step 9C - Generate and install the updated DOF config

1. Click **Generate Config** at the top
2. Download `directoutputconfig.zip`
3. Extract the ZIP
4. Copy all extracted files (typically about 4 files) into your DOF `Config` folder

Example:

```
{Your DOF Install Folder}\Config
```

For many users this may be something like:

```
C:\DirectOutput\Config
```

But your DOF install location might be different.

### Step 9D - Restart and test

1. Restart the **Virtual DOF Matrix** app
2. Launch the table

If everything is set up correctly, you should now see the custom or GIF-driven light effects for that table.

---

## 10) Popper setup

You can launch the app once at Popper startup, then control visibility during table launch and exit.

### Command format

```bat
"C:\vPin\VirtualLED\VirtualDofMatrix.App.exe" --command <action> [optional args]
```

Replace `C:\vPin\VirtualLED` with your real install path.

Supported actions:

- `show`
- `hide`
- `frontend-return`
- `table-launch [CUSTOM1] [CUSTOM2] [CUSTOM3] [CUSTOM4]`
- `table-launch --default-show-virtual-led [CUSTOM1] [CUSTOM2] [CUSTOM3] [CUSTOM4]`

### Recommended Popper flow

1. **Popper Startup Script**

   ```bat
   START "" "C:\vPin\VirtualLED\VirtualDofMatrix.App.exe"
   ```

2. **Table launch style (choose one)**

   **Choice A: Hide by default, show only selected tables**

   ```bat
   "C:\vPin\VirtualLED\VirtualDofMatrix.App.exe" --command table-launch "[CUSTOM1]" "[CUSTOM2]" "[CUSTOM3]" "[CUSTOM4]"
   ```

   - If no custom var contains `ShowVirtualLED`, the matrix stays hidden during table launch
   - Put `ShowVirtualLED` in any custom var to keep it visible
   - `HideVirtualLED` still forces hidden

   **Choice B: Show by default, hide selected tables**

   ```bat
   "C:\vPin\VirtualLED\VirtualDofMatrix.App.exe" --command table-launch --default-show-virtual-led "[CUSTOM1]" "[CUSTOM2]" "[CUSTOM3]" "[CUSTOM4]"
   ```

   - The matrix stays visible unless a custom var contains `HideVirtualLED`

3. **VPX Close Script**

   ```bat
   "C:\vPin\VirtualLED\VirtualDofMatrix.App.exe" --command frontend-return
   ```

4. **Popper Exit Script**

   ```bat
   taskkill /IM "VirtualDofMatrix.App.exe" /F
   ```

### Optional manual commands

```bat
"C:\vPin\VirtualLED\VirtualDofMatrix.App.exe" --command hide
"C:\vPin\VirtualLED\VirtualDofMatrix.App.exe" --command show
```

---

## 11) Troubleshooting

### App opens but no animation

- Start the app first, then VPX or your front-end
- Confirm both DOF DLLs were replaced from this package:
  - `C:\DirectOutput\x64\DirectOutput.dll`
  - `C:\DirectOutput\x86\DirectOutput.dll`
- Confirm the controller name or pipe value matches between `Cabinet.xml` and `settings.json`
- Make sure you do **not** have duplicate copies of these files elsewhere on your hard drive:
  - `DOFGlobalConfig.xml`
  - `GlobalConfig_B2SServer.xml`
  - `toys.ini`

Check common locations such as:

- Your VPX Tables folder
- `PinUPSystem`
- Old backup or test folders

If duplicate copies exist, DOF may be reading the wrong files.

If all else fails:

1. Back up your `{DOFInstallDir}\Config` folder first
2. Delete the contents of `{DOFInstallDir}\Config`
3. Re-run `DOFConfigSetup.bat`

### Main toy works, additional toys stay dark

- In **Settings -> Virtual Toys**, make sure each toy is enabled
- Re-check toy size and layout settings
- Confirm DOF is actually outputting data for those extra toy ranges
- Click **OK** after every change

### I changed toy settings and they reverted later

- This usually means the changes were not saved
- Open Settings, re-apply the changes, and click **OK**

### Works in VPX but not Popper (or reverse)

- Re-check both DOF DLL locations:
  - `C:\DirectOutput\x64\DirectOutput.dll`
  - `C:\DirectOutput\x86\DirectOutput.dll`
- If you want automatic startup, configure Popper to launch the app as shown in Step 10

### How testers can retrieve logs

1. Open **Settings -> Advanced**.
2. Click **Open debug.log** to open the active log file directly.
3. If needed, click **Open Logs Folder** to open the containing folder for easy upload/sharing.
4. If you see **"No log file yet. Start emulator and retry."**, start a table so the emulator produces runtime activity, then try again.

---

## Final reminder

For normal setup, manage toys through **Settings -> Virtual Toys** and save with **OK**.

That is the recommended workflow for reliable day-to-day use.
