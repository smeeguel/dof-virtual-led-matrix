# Driver Workspace

This workspace contains a **KMDF** virtual serial driver skeleton for local service-managed virtual COM pair provisioning.

## Required prerequisites (for `ntddk.h`)

If you see:

- `C1083: Cannot open include file: 'ntddk.h'`

then your environment is missing WDK integration.

Install all of the following:

1. **Visual Studio 2022** (Desktop development with C++)
2. **Windows SDK 10/11** (same major version as your target)
3. **Windows Driver Kit (WDK)** matching your Visual Studio generation

After installation:

1. Re-open Visual Studio.
2. Right-click `VirtualDofMatrixVirtualSerial.vcxproj` -> **Retarget Projects**.
3. Select installed Windows SDK + `WindowsKernelModeDriver10.0` toolset.
4. Build `x64` (`Debug` or `Release`).

> `ntddk.h` and other kernel headers are provided by WDK, not standard C++ workloads.

## Layout

- `VirtualDofMatrixVirtualSerial/` - Visual Studio KMDF project skeleton.
- `scripts/` - install/uninstall helper scripts for INF package handling.
- `signing/` - signing pipeline placeholders and process docs.

The design goal is to expose linked COM endpoint pairs (Tx/Rx) that appear under **Ports (COM & LPT)**.
