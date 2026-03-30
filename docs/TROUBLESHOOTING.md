# Troubleshooting

## Log location

Structured logs are written to:

`%ProgramData%\VirtualDofMatrix\logs`

Copy diagnostics from the app using **Copy diagnostics** in the debug panel.

## Signature blocked / secure boot failures

If health checks or provisioning return signing-related failures:

### Symptoms
- `SigningBlocked`
- `SecureBootConflict`
- Service health reports driver unavailable after install

### What it means
- `SigningBlocked`: Windows policy rejected driver signing trust chain.
- `SecureBootConflict`: Secure Boot / code integrity policy blocked loading the driver binary.

### Recommended steps
1. Confirm the installed package is the **Microsoft-attested signed** driver build.
2. Verify the catalog (`.cat`) matches the installed `.sys` and INF version.
3. Check Windows Event Viewer:
   - **Applications and Services Logs > Microsoft > Windows > CodeIntegrity > Operational**
   - **System** log entries for service/driver load failures.
4. Use the release installer path, which does not modify test-signing or Secure Boot settings.
5. Reinstall using installer tooling and rerun post-install health check.

### `pnputil` reports missing digital signature metadata

If installer output shows:

- `Failed to add driver package: The third-party INF does not contain digital signature information.`
- `Driver installation failed with exit code -536870353`

then Windows did not find a trusted signed catalog for the staged INF package.

Recommended fixes:
1. Ensure build output includes matching `INF + SYS + CAT` for the same version.
2. Ensure the `CAT` is signed and trusted for the target machine policy (attestation/production for normal installs).
3. Run `installer/scripts/verify-release-driver-signature.ps1` against your INF/SYS/CAT package.
4. Re-run `installer/scripts/install.ps1` after signature preflight succeeds.

## Service unavailable

- Ensure `VirtualDofMatrixProvisioning` service exists and is running.
- Validate named pipe `VirtualDofMatrix.Provisioning.v1` is reachable.
- Review `%ProgramData%\VirtualDofMatrix\logs` for connection/fallback/failure events.

## Port conflicts

- Check if target COM ports are already assigned.
- For temporary fallback testing, set `virtualCom.disableFallbackToProcessCommand` to `false`.
- Prefer returning to `serial.virtualProviderMode = "service"` for normal operation.


## Driver build error: `ntddk.h` not found

If Visual Studio reports `C1083 Cannot open include file: ntddk.h`, install/repair:

- Visual Studio (current supported version, e.g. VS 2026) with Desktop development with C++
- Windows SDK
- WDK (matching your installed Visual Studio generation)

Then retarget the `VirtualDofMatrixVirtualSerial` project to the installed SDK + `WindowsKernelModeDriver10.0` toolset and rebuild x64.
