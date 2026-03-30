# VirtualDofMatrix.Service

`VirtualDofMatrix.Service` is a Windows Service intended to run as **LocalSystem** (elevated) and broker virtual COM pair operations between user-mode apps and the installed KMDF driver.

## Responsibilities

- Talk to installed virtual serial driver service (`VirtualDofMatrixSerial`).
- Create/remove linked COM pairs.
- Persist known pair metadata at:
  - `%ProgramData%\VirtualDofMatrix\pairs.json`
- Expose local IPC surface for app calls.

## IPC transport

Current transport: **Named Pipe** `VirtualDofMatrix.Provisioning.v1`.

ACL is restricted to:
- LocalSystem (full control)
- Builtin Administrators (read/write)
- Authenticated Users (read/write)

## Request commands

JSON line requests:

- `create` + `txPort` + `rxPort`
- `delete` + `txPort` + `rxPort`
- `list`
- `health`

## Structured error codes

- `DriverMissing`
- `SigningBlocked`
- `SecureBootConflict`
- `PortConflict`

(plus validation/internal codes for protocol/runtime failures).
