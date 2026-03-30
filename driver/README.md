# Driver Workspace

This workspace contains a KMDF virtual serial driver skeleton for local service-managed virtual COM pair provisioning.

## Layout

- `VirtualDofMatrixVirtualSerial/` - Visual Studio KMDF project skeleton.
- `scripts/` - install/uninstall helper scripts for INF package handling.
- `signing/` - signing pipeline placeholders and process docs.

The design goal is to expose linked COM endpoint pairs (Tx/Rx) that appear under **Ports (COM & LPT)**.
