# Virtual DOF Matrix Driver Plan

## Purpose

The `driver/` workspace contains the service-managed virtual serial pair driver skeleton used by the app provisioning layer.
The target is a **KMDF-based virtual serial bus + child PDO model** that exposes **linked COM endpoints in pairs**.

## Target OS

- Windows 10 x64 (22H2 or newer recommended)
- Windows 11 x64

## Architecture goals

1. Driver creates a virtual bus/FDO that can materialize pair instances.
2. Each pair creates two child devices (A/B endpoints) surfaced under **Ports (COM & LPT)** in Device Manager.
3. Writes to endpoint A are readable from endpoint B and vice-versa.
4. Pair metadata is persisted so a provisioning service can enumerate existing pairs.

## Signing requirements

Windows kernel-mode deployment requires signing:

- **Development/test:** Test certificate + test signing mode.
- **Distribution:** EV code-signing certificate and Microsoft attestation signing pipeline.

The repository includes placeholders for both flows under `driver/signing/`.
No private keys or real cert material should be committed.

## Build + package flow (planned)

1. Build KMDF driver project (x64, Release).
2. Stage `.sys`, `.inf`, and `.cat` into package output.
3. Sign catalog using EV cert.
4. Submit package for Microsoft attestation signing.
5. Publish signed package and install scripts.

## Installation notes

Use the scripts in `driver/scripts/`:

- `install-driver.ps1` stages and installs INF package.
- `uninstall-driver.ps1` removes OEM INF and rescans devices.

Both scripts require elevated PowerShell.
