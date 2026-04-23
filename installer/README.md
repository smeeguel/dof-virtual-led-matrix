# Installer projects

This folder stages the MSI/Burn installer path while the existing ZIP release flow remains in place.

## Build entry points

- `installer/VirtualDofMatrix.Setup/VirtualDofMatrix.Msi.csproj` builds the MSI package.
- `installer/VirtualDofMatrix.Setup/VirtualDofMatrix.Bundle.csproj` builds the Burn bootstrapper (`setup.exe`).
- `installer/CustomActions/CustomActions.csproj` builds elevated C# custom actions consumed by the MSI.

## CI-friendly build flow

1. Publish app binaries (`dotnet publish`) to a known directory.
2. Build bundle with `InstallerAppPublishDir` pointing at that publish directory.

Example:

```powershell
dotnet build installer/VirtualDofMatrix.Setup/VirtualDofMatrix.Bundle.csproj `
  -c Release `
  /p:Version=0.1.0 `
  /p:InstallerAppPublishDir="src/VirtualDofMatrix.App/bin/Release/net8.0-windows/win-x64/publish/"
```

## Expected artifacts

- MSI: `installer/VirtualDofMatrix.Setup/bin/<Configuration>/VirtualDofMatrix.Msi.msi`
- Bundle EXE: `installer/VirtualDofMatrix.Setup/bin/<Configuration>/setup.exe`
- Custom actions: `installer/CustomActions/bin/<Configuration>/net8.0-windows/`

## Notes

- Current release ZIP packaging in `tools/packaging/package-release.ps1` is intentionally unchanged during this transition.
- Install the **WiX Toolset Visual Studio 2022/2026 extension** so Visual Studio can load WiX SDK projects.
- WiX MSBuild SDK version is pinned in repository `global.json` (`WixToolset.Sdk` 6.0.2) so Visual Studio and `dotnet` resolve the same installer SDK without forcing a specific .NET SDK feature band.
