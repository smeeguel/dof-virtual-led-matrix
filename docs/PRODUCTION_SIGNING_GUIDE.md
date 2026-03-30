# Production Driver Signing & Release Guide (Secure Boot Friendly)

**Last updated:** March 30, 2026

This guide is for a **solo developer** shipping a **non-commercial community app** and wanting the fastest path to installable virtual COM drivers that work with **Secure Boot enabled**.

---

## Goal

Ship an installer that works on normal Windows 10/11 systems **without** asking users to:

- disable Secure Boot,
- enable test-signing mode,
- install local dev certificates.

To do that, your driver package must be production-signed and trusted by Windows policy.

---

## What files must exist before install

A release driver package must contain matching versions of:

1. `VirtualDofMatrixVirtualSerial.inf`
2. `VirtualDofMatrixVirtualSerial.sys`
3. `VirtualDofMatrixVirtualSerial.cat`

If any one of these is missing or mismatched, install will fail.

---

## Fastest A=>B path (high-level)

1. Build release driver artifacts (`INF/SYS/CAT`).
2. Submit package to Microsoft Hardware Dev Center signing workflow.
3. Download signed output artifacts.
4. Run signature preflight script locally.
5. Run release installer (`installer/scripts/install.ps1`).
6. Validate COM pair provisioning/service health.

---

## Step-by-step (beginner, no skipped steps)

## Step 1 - Build your driver package

1. Open `driver/VirtualDofMatrixVirtualSerial/VirtualDofMatrixVirtualSerial.vcxproj` in Visual Studio.
2. Select `x64` + `Release`.
3. Build the project.
4. Locate build outputs and confirm `INF/SYS/CAT` are all present.

If `.cat` is missing, fix build/packaging first before submission.

## Step 2 - Set up Microsoft Hardware submission account

1. Sign in to Microsoft Partner Center with your developer account.
2. Complete Windows Hardware Developer Program registration.
3. Complete any org verification/profile requirements shown in dashboard.
4. Ensure certificate requirements for submission are satisfied in your account setup.

> This onboarding is one-time; future driver submissions are faster.

## Step 3 - Create a hardware submission

1. In Hardware Dashboard, create a new driver submission.
2. Upload your package (the package that includes your INF/SYS/CAT outputs).
3. Fill required metadata exactly (publisher/provider/version consistency matters).
4. Submit and wait for Microsoft processing.

## Step 4 - Download signed artifacts

1. After submission completes, download the signed package files.
2. Replace local unsigned artifacts in your release staging folder.
3. Keep filenames and versions aligned with installer expectations.

## Step 5 - Validate signatures before install

Run from elevated PowerShell in repo root:

```powershell
.\installer\scripts\verify-release-driver-signature.ps1 `
  -DriverInfPath .\driver\VirtualDofMatrixVirtualSerial\inf\VirtualDofMatrixVirtualSerial.inf `
  -DriverSysPath <path-to-signed-sys> `
  -DriverCatPath <path-to-signed-cat>
```

Expected result: `Release signature preflight checks passed.`

## Step 6 - Run release installer

```powershell
cd .\installer\scripts
.\install.ps1 -DriverSysPath <path-to-signed-sys> -DriverCatPath <path-to-signed-cat>
```

Installer behavior in release mode:

- does **not** call `bcdedit /set testsigning on`,
- does **not** create local certs,
- does **not** ask users to disable Secure Boot.

## Step 7 - Validate runtime health

1. Confirm service is running: `VirtualDofMatrixProvisioning`.
2. Run installer health check if needed: `installer/scripts/health-check.ps1`.
3. Start app and verify provisioning + COM behavior.

---

## Recommended release checklist

Before publishing to the community:

- [ ] Signed `INF/SYS/CAT` package is finalized.
- [ ] `verify-release-driver-signature.ps1` passes on a clean machine.
- [ ] Install/uninstall tested on Secure Boot-enabled Windows 10 and 11.
- [ ] Service health check passes.
- [ ] App successfully creates/uses COM pair through service path.
- [ ] README/docs explicitly state no BIOS/Secure Boot changes are required.

---

## Common mistakes to avoid

1. **Using old CAT with new SYS** (version mismatch).
2. **Testing only on dev machine** that already has trust artifacts.
3. **Publishing test-signed bits** to community users.
4. **Skipping signature preflight** before `pnputil`.

---

## If submission is blocked or delayed

1. Keep previous known-good signed release package as fallback.
2. Do not publish unsigned/test-signed package as official release.
3. Use troubleshooting docs (`docs/TROUBLESHOOTING.md`) and inspect Code Integrity logs on validation machine.

---

## Notes for this project

- This project uses a release-first installer path to stay Secure Boot friendly.
- Lab-only test-signing automation is intentionally excluded from release install flow.
