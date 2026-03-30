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
4. On Secure Boot-enabled systems, avoid test-signed binaries.
5. If testing with test-signed packages, use an isolated lab machine and explicitly configure test mode.
6. Reinstall using installer tooling and rerun post-install health check.

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

- Visual Studio 2022 (Desktop development with C++)
- Windows SDK
- WDK (matching VS2022)

Then retarget the `VirtualDofMatrixVirtualSerial` project to the installed SDK + `WindowsKernelModeDriver10.0` toolset and rebuild x64.
