# Installer projects

This folder stages the MSI installer path while the existing ZIP release flow remains in place.

## Build entry points

- `installer/VirtualDofMatrix.Setup/VirtualDofMatrix.Msi.csproj` builds the MSI package.
- `installer/VirtualDofMatrix.Setup/VirtualDofMatrix.Bundle.csproj` is optional for local Burn/bootstrapper experiments and is not required by CI packaging.
- `installer/CustomActions/CustomActions.csproj` builds elevated C# custom actions consumed by the MSI.

## CI-friendly build flow

1. Publish app binaries (`dotnet publish`) to a known directory.
2. Build the MSI with `InstallerAppPublishDir` pointing at that publish directory.

Example:

```powershell
dotnet build installer/VirtualDofMatrix.Setup/VirtualDofMatrix.Msi.csproj `
  -c Release `
  /p:Version=0.1.0 `
  /p:InstallerAppPublishDir="src/VirtualDofMatrix.App/bin/Release/net8.0-windows/win-x64/publish/"
```

## Expected artifacts

- MSI: `installer/VirtualDofMatrix.Setup/bin/<Configuration>/VirtualDOFMatrixSetup.msi` (single-file MSI; cabinets are embedded)
- Bundle EXE (optional/local only): depends on local Burn configuration
- Custom actions: `installer/CustomActions/bin/<Configuration>/net8.0-windows/`

## Notes

- GitHub release packaging now publishes the MSI directly as the installer artifact and keeps the ZIP package flow unchanged.
- Install the **WiX Toolset Visual Studio 2022/2026 extension** so Visual Studio can load WiX SDK projects.
- WiX MSBuild SDK version is pinned in repository `global.json` (`WixToolset.Sdk` 6.0.2) so Visual Studio and `dotnet` resolve the same installer SDK without forcing a specific .NET SDK feature band.
- The MSI license dialog is sourced from `installer/VirtualDofMatrix.Setup/Eula.rtf`, which mirrors the project `EULA.txt` text for installer UI compatibility.
