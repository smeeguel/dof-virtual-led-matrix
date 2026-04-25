# Virtual DOF Matrix by Migs: Setup Guide

## Table of Contents

- [What this app does](#what-this-app-does)
- [Quick Start (Installer)](#quick-start-installer)
- [Requirements](#requirements)
- [Install order (DOF, then installer)](#install-order-dof-then-installer)
- [First launch order](#first-launch-order)
- [Basic toy setup](#basic-toy-setup)
- [Custom GIF / community light shows (DOF Config Tool)](#custom-gif--community-light-shows-dof-config-tool)
- [Optional advanced setup](#optional-advanced-setup)
- [Popper setup](#popper-setup)
- [Troubleshooting](#troubleshooting)
- [License/disclaimers](#licensedisclaimers)

This guide is written for everyday cabinet owners, including first-time DOF users.

GitHub repo:
https://github.com/smeeguel/dof-virtual-led-matrix

Need help or want to share feedback? Use this simple form:
https://forms.gle/1CWDqyuZR86VikSF6


---

## What this app does

Virtual DOF Matrix displays DOF LED output in one or more virtual on-screen windows, such as a backglass matrix or addressable LED strips.

You do not need to understand DOF deeply to use this app. Just follow the steps in order.

---

## Quick Start (Installer)

1. Install DOF
2. Run the Virtual DOF Matrix installer
3. Launch Virtual DOF Matrix
4. Launch VPX/table

If it does not work, come back to the troubleshooting section.

---

## Requirements

You need:

- Windows
- DOF installed before running the Virtual DOF Matrix installer
- Visual Pinball X (VPX), if you want to launch tables
- PinUP Popper, only if you use a front-end workflow (optional)

---

## Install order (DOF, then installer)

This order matters:

1. Install DOF first.
2. Run the Virtual DOF Matrix installer.
3. Follow the installer prompts.

### Install DOF

Download DOF from:

- http://mjrnet.org/pinscape/dll-updates.html#DOF

That page includes full DOF installers for both x64 and x86.

For most users, the normal DOF folder is:

```text
C:\DirectOutput
```

If you already have DOF installed, it is a good idea to back up your DOF folder before continuing.

### Run the Virtual DOF Matrix installer

After DOF is installed:

1. Run the Virtual DOF Matrix installer.
2. Choose the setup option that matches your cabinet when prompted.
3. Finish the installer.

You should not need to copy files, replace DLLs, edit XML, or run setup scripts manually for normal installation.

---

## First launch order

Always start in this order:

1. Launch **Virtual DOF Matrix**.
2. Launch Popper or a VPX table.

If VPX, Popper, or DOF starts first, the app might miss the initial connection.

If you use the Popper startup automation below, Popper can launch the app for you.

---

## Basic toy setup

For normal use, manage virtual toys in:

- **Settings -> Virtual Toys**

From there you can:

- Add toys
- Edit toys
- Enable or disable toys
- Move and resize toy windows
- Click **OK** to save changes

If you are new, this is the recommended workflow.

### Add a new toy

Use this when creating a second, third, or fourth virtual output.

1. Launch the app.
2. Open **Settings**.
3. Click **Virtual Toys**.
4. Click **Add Toy**.
5. Choose **Single strip** or **Full matrix**.
6. Enter a toy name you will recognize later, such as `Strip2` or `Matrix2`.
7. Set the size and layout options.
8. Make sure the toy is enabled.
9. Click **Save**, then **OK**.
10. Launch a table and confirm the toy animates.

For a standard 32x8 DMD-style matrix, use:

- Width: `32`
- Height: `8`
- Mapping: `TopDownAlternateRightLeft`

### Edit an existing toy

1. Open **Settings -> Virtual Toys**.
2. Select the toy you want to edit.
3. Change the size, position, visual settings, or enabled state.
4. Click **OK** to save.
5. If the app asks to update your cabinet settings, review the summary and confirm only if it looks right.

### Behavior notes

- Each toy row has an on/off switch.
- Turning a toy off hides its viewer window.
- At least one toy must stay enabled.
- If a table is running, toy visibility can be saved for that table without changing your global default.
- You can right-click a toy window and choose **Disable for &lt;active table&gt;**.
- You can keep Settings open while moving or resizing toy windows.
- Window position and size save automatically.
- Choosing **Exit** from any viewer window closes the full app.

### Table-specific visibility overrides

Most users do not need to edit override files manually.

When you toggle toy visibility or move/resize toy windows while a table is active, the app can save those choices for that table.

Tip: if you want to keep the current table layout but make it your global default, right-click that toy window and choose **Save &lt;toyname&gt; global position**.

### Custom GIF / community light shows (DOF Config Tool)

This step is optional, but recommended if you want enhanced light shows, including community-created GIF animations.

Open the DOF Config Tool:

- https://configtool.vpuniverse.com/app/home

Important notes:

- Community-created GIF / MX animations are not included with this app.
- You can download them directly from the DOF Config Tool.
- Before applying custom MX content, import one of the included cabinet JSON files into the DOF Config Tool so it knows which virtual matrix/strip layout you are using.
- After changing DOF Config Tool settings, generate and download your updated DOF config files, then place them in your DOF `Config` folder.

#### Import a Virtual DOF Matrix cabinet

Do this once before downloading custom GIF / MX content.

1. Open the DOF Config Tool.
2. Go to **Cabinet -> Manage**.
3. Create a cabinet, or select the cabinet you want to use.
4. Click the three dots next to that cabinet.
5. Choose **Import Cabinet...**.
6. Browse to the `ConfigToolCabinets` folder in your Virtual DOF Matrix install folder.

Example:

```text
C:\vPin\VirtualLED\ConfigToolCabinets
```

Your app install folder may be different. The key point is that `ConfigToolCabinets` is directly inside the Virtual DOF Matrix install folder.

Choose the cabinet file that matches your setup:

- **Single matrix only**
  - `Cabinet_SingleMatrix.json`
- **Matrix + 3 LED strips**
  - `Cabinet_BackMatrixPlus3LEDStrips.json`

After importing, continue with one of the options below.

#### Easiest option: apply custom MX content to all tables

This is the easiest path for most beginners.

1. Go to **Tables -> Configurations**.
2. Click the three dots (**More Actions...**).
3. Select **Apply MX Configuration to All Tables**.
4. Confirm the warning popup.
5. Click **Download Zip of all MX Gif's**.
6. Extract `MX_Images.zip` into your DOF `Config` folder.
7. Back in the Config Tool, click **Generate Config** at the top.
8. Download `directoutputconfig.zip`.
9. Extract that ZIP too.
10. Copy all extracted config files into your DOF `Config` folder.
11. Restart Virtual DOF Matrix, then restart VPX, Popper, or any table session you already had open.

For many users, the DOF `Config` folder is:

```text
C:\DirectOutput\Config
```

Your DOF install location might be different.

#### Optional advanced route: customize individual tables only

Use this path only if you want to customize one table at a time instead of applying MX content to all tables.

1. Go to **Tables -> Configurations**.
2. Search for the table you want to customize.
3. Open that table's configuration.
4. Expand **Special MX Effect Configurations (optional)**, if available.
5. Download any embedded GIFs you want.
6. Click **Apply MX Effects to your User Configuration**.
7. Click **Save / Update**.
8. Click **Generate Config** at the top.
9. Download and extract `directoutputconfig.zip`.
10. Copy the extracted config files into your DOF `Config` folder.
11. Restart Virtual DOF Matrix, then restart VPX, Popper, or any table session you already had open.

---

## Optional advanced setup

> **Most users should skip this section.**
>
> Normal installation is handled by the installer. Do not edit XML files unless you have a hybrid cabinet, custom DOF routing, or a support helper specifically asks you to check something here.

### Cabinet.xml for combination setups (virtual + hardware)

Use this only if you have virtual toys plus physical hardware toys, custom routing, or older custom DOF files you need to preserve.

Before making changes, back up your DOF `Config` folder.

For most users, that folder is:

```text
C:\DirectOutput\Config
```

Advanced checks:

- Your DOF `Cabinet.xml` should include the virtual output your setup needs.
- The virtual controller name in DOF should match the app's configured pipe name.
- If **Auto-update Cabinet on resolution change** is enabled, the app will show a summary before saving changes.

If you are not sure what to change, do not guess. Use the support form and include your logs.

### Extra toys and custom routing

Adding toys in the app is the first step, but DOF must also send data for those toy ranges.

For custom multi-toy setups, these need to agree with each other:

1. The toys you created in Virtual DOF Matrix
2. Your DOF Config Tool cabinet setup
3. Your generated DOF config files

If your main toy works but extra toys stay dark, start with the troubleshooting section before editing files manually.

---

## Popper setup

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

   - If no custom var contains `ShowVirtualLED`, the matrix stays hidden during table launch.
   - Put `ShowVirtualLED` in any custom var to keep it visible.
   - `HideVirtualLED` still forces hidden.

   **Choice B: Show by default, hide selected tables**

   ```bat
   "C:\vPin\VirtualLED\VirtualDofMatrix.App.exe" --command table-launch --default-show-virtual-led "[CUSTOM1]" "[CUSTOM2]" "[CUSTOM3]" "[CUSTOM4]"
   ```

   - The matrix stays visible unless a custom var contains `HideVirtualLED`.

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

## Troubleshooting

### App opens but no animation

Try these checks in order:

1. Confirm DOF is installed.
2. Re-run the Virtual DOF Matrix installer and choose repair/reinstall if offered.
3. Start Virtual DOF Matrix first, then launch VPX or your front-end.
4. Confirm your DOF folder is where you expect it to be, usually:

   ```text
   C:\DirectOutput
   ```

5. Make sure you do **not** have duplicate copies of common DOF config files elsewhere on your hard drive.

Common conflict files include:

- `DOFGlobalConfig.xml`
- `GlobalConfig_B2SServer.xml`
- `toys.ini`

Check common locations such as:

- Your VPX Tables folder
- `PinUPSystem`
- Old backup or test folders

If duplicate copies exist, DOF may be reading the wrong files.

### Main toy works, additional toys stay dark

- In **Settings -> Virtual Toys**, make sure each toy is enabled.
- Re-check toy size and layout settings.
- Confirm your DOF Config Tool setup includes output for those extra toys.
- Click **OK** after every change.

### I changed toy settings and they reverted later

- This usually means the changes were not saved.
- Open Settings, re-apply the changes, and click **OK**.

### Works in VPX but not Popper (or reverse)

- Confirm Popper launches Virtual DOF Matrix before launching the table.
- Check that your Popper commands use the correct app path.
- If you want automatic startup, configure Popper to launch the app as shown in the Popper setup section.

### How to collect logs for support

1. Open **Settings -> Advanced**.
2. Click **Open debug.log** to open the active log file directly.
3. If needed, click **Open Logs Folder** to open the containing folder for easy upload/sharing.
4. If you see **"No log file yet. Start emulator and retry."**, start a table so the app produces runtime activity, then try again.

---

## License/disclaimers

Virtual DOF Matrix is open-source under the MIT License.

You are free to:

- Use, modify, and share the software
- Contribute improvements

Please note:

- Modified versions must not be redistributed under the same name.
- This project depends on third-party tools such as DOF, VPX, and PinUP Popper, which are not included with this project.
- Community-created GIF / MX assets are not distributed with this project.
- This software interacts with DOF configuration files and external systems, so use it at your own risk.
- Before making major changes, it is strongly recommended to back up your DOF folder, usually:

```text
C:\DirectOutput
```

This application does not collect or transmit any user data. Any logs it creates remain local to your machine.

This is a community-driven project. If you run into issues or want to suggest features, use the feedback form linked at the top of this guide or visit the GitHub repository for updates and discussions.
