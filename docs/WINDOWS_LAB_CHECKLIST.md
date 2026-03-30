# Windows Lab Validation Checklist (Manual / Integration)

Use this checklist on a Windows 10/11 x64 lab machine with signed driver + service package installed.

## 1) Pair create/delete success
- [ ] Start `VirtualDofMatrixProvisioning` service.
- [ ] Send `create` request (`COM1`/`COM2`) over `VirtualDofMatrix.Provisioning.v1`.
- [ ] Verify pair appears in service `list` response.
- [ ] Send `delete` request and verify pair is removed.

## 2) Persisted-pair reuse on restart
- [ ] Create a pair, then restart service.
- [ ] Verify persisted metadata exists at `%ProgramData%\VirtualDofMatrix\pairs.json`.
- [ ] Verify `list` reports prior pair after restart.

## 3) Occupied-port fallback behavior
- [ ] Configure app with `serial.virtualProviderMode = service` and `virtualCom.disableFallbackToProcessCommand = false`.
- [ ] Force service create failure for occupied port (e.g., reserve COM port in lab).
- [ ] Verify app logs service failure and tries `processCommand` fallback.

## 4) Service unavailable behavior
- [ ] Stop provisioning service.
- [ ] Launch app in `service` mode with fallback disabled.
- [ ] Verify startup fails with explicit health/provisioning error.

## 5) Driver missing / signature blocked behavior
- [ ] Uninstall driver package and request `health`.
- [ ] Verify structured error maps to `DriverMissing`.
- [ ] Install unsigned/blocked package in controlled lab and verify error maps to signing-related code.

## 6) Serial host bind/rebind after pair creation
- [ ] Provision pair from app startup.
- [ ] Verify serial host binds to configured RX endpoint.
- [ ] Delete/recreate pair and relaunch app.
- [ ] Verify serial host rebinds and DOF traffic resumes.
