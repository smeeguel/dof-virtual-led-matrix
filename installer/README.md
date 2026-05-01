# Installer project

`VirtualDofMatrix.Installer` is the standalone Windows installer for Virtual DOF Matrix.

## CI-friendly build flow

1. Publish the app payload first:

```powershell
dotnet publish src/VirtualDofMatrix.App/VirtualDofMatrix.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  /p:PublishSingleFile=true
```

2. Publish the installer and point `InstallerAppPublishDir` at the app publish output:

```powershell
dotnet publish installer/VirtualDofMatrix.Installer/VirtualDofMatrix.Installer.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  /p:PublishSingleFile=true `
  /p:InstallerAppPublishDir="src/VirtualDofMatrix.App/bin/Release/net8.0-windows/win-x64/publish/"
```

## Expected artifact

The installer artifact is a single file:

```text
installer/VirtualDofMatrix.Installer/bin/x64/Release/net8.0-windows/win-x64/publish/VirtualDofMatrix.Installer.exe
```

The app payload and DOF payload are embedded as installer resources. They should not be packaged as a sibling `payload` directory or wrapped in an installer ZIP.
