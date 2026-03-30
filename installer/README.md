# Installer Tooling

This folder contains deployment scaffolding for production packaging with WiX/MSIX-compatible workflows.

## Goals covered

- Install signed virtual serial driver package.
- Install/register/start `VirtualDofMatrixProvisioning` Windows service.
- Configure service recovery policies.
- Run post-install health validation.
- Provide uninstall flow with optional active pair cleanup.

## Layout

- `wix/VirtualDofMatrixInstaller.wxs` - WiX Burn bootstrapper skeleton.
- `scripts/install.ps1` - scripted release install for local/admin deployment.
- `scripts/verify-release-driver-signature.ps1` - preflight signature verification (`INF` + `SYS` + `CAT`).
- `scripts/uninstall.ps1` - scripted uninstall path.
- `scripts/health-check.ps1` - post-install health checks.

## Notes

- Driver package and service MSI artifacts referenced here are placeholders and should be replaced with signed build outputs.
- Service account is set to `LocalSystem` by default.
- `install.ps1` is now release-first and does not toggle `testsigning`, modify Secure Boot behavior, or create local test certificates.
- Install requires pre-signed production artifacts (`INF` + `SYS` + `CAT`). Signature preflight is run before `pnputil`.
