# Installer projects

This folder stages the MSI/Burn installer path while the existing ZIP release flow remains in place.

## Build entry points

- `installer/VirtualDofMatrix.Setup/VirtualDofMatrix.Msi.csproj` builds the MSI package.
- `installer/VirtualDofMatrix.Setup/VirtualDofMatrix.Bundle.csproj` builds the Burn bootstrapper (`setup.exe`).
- `installer/CustomActions/CustomActions.csproj` builds elevated C# custom actions consumed by the MSI.

## Expected artifacts

- MSI: `installer/VirtualDofMatrix.Setup/bin/<Configuration>/VirtualDofMatrix.Msi.msi`
- Bundle EXE: `installer/VirtualDofMatrix.Setup/bin/<Configuration>/setup.exe`
- Custom actions: `installer/CustomActions/bin/<Configuration>/net8.0-windows/`

## Notes

- Current release ZIP packaging in `tools/packaging/package-release.ps1` is intentionally unchanged during this transition.


- WiX MSBuild SDK version is pinned in repository `global.json` (`WixToolset.Sdk` 4.0.5) so Visual Studio and `dotnet` resolve the same installer SDK.
