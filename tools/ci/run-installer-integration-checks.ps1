param(
  [Parameter(Mandatory = $true)]
  [string]$InstallerPath
)

$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$logRoot = Join-Path $repoRoot 'artifacts\ci-logs'
New-Item -Path $logRoot -ItemType Directory -Force | Out-Null

function Assert-FileExists {
  param(
    [Parameter(Mandatory = $true)][string]$Path,
    [Parameter(Mandatory = $true)][string]$Reason
  )
  if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
    throw "Expected file missing: $Path ($Reason)."
  }
}

function Assert-RegistryValueExists {
  param(
    [Parameter(Mandatory = $true)][string]$KeyPath,
    [Parameter(Mandatory = $true)][string]$ValueName,
    [Parameter(Mandatory = $true)][string]$Reason
  )
  $key = Get-ItemProperty -LiteralPath $KeyPath -Name $ValueName -ErrorAction SilentlyContinue
  if ($null -eq $key) {
    throw "Expected registry value missing: $KeyPath\$ValueName ($Reason)."
  }
}

function New-FakeDofRoot {
  param([string]$Name)
  $fakeRoot = Join-Path $env:RUNNER_TEMP ("fake-dof-{0}-{1}" -f $Name, [guid]::NewGuid().ToString('N'))
  $configPath = Join-Path $fakeRoot 'Config'
  New-Item -Path $configPath -ItemType Directory -Force | Out-Null
  Set-Content -LiteralPath (Join-Path $configPath 'preexisting-user-config.txt') -Value 'original-config-value' -Encoding ascii
  return @{ Root = $fakeRoot; Config = $configPath }
}

function Invoke-SilentInstallAndValidate {
  param([string]$InstallerExePath)

  $paths = New-FakeDofRoot -Name 'silent'
  $installDir = Join-Path $env:RUNNER_TEMP ("test-install-{0}" -f [guid]::NewGuid().ToString('N'))
  $backupPath = Join-Path $paths.Root 'Backups\ci-backup'
  $installLog = Join-Path $logRoot 'installer-silent-install.log'

  Write-Host "Running silent install: installer=$InstallerExePath"
  Write-Host "  install-folder=$installDir"
  Write-Host "  dof-config-path=$($paths.Config)"

  $proc = Start-Process -FilePath $InstallerExePath -ArgumentList @(
    '--silent',
    '--install-folder', $installDir,
    '--dof-config-path', $paths.Config,
    '--template', 'matrix_plus_3_strips',
    '--backup-path', $backupPath
  ) -PassThru -Wait -RedirectStandardOutput $installLog -RedirectStandardError "$installLog.err.log"

  if ($proc.ExitCode -ne 0) {
    if (Test-Path $installLog) { Get-Content $installLog | Write-Host }
    if (Test-Path "$installLog.err.log") { Get-Content "$installLog.err.log" | Write-Host }
    throw "Silent installer exited with code $($proc.ExitCode). See $installLog."
  }

  # Verify backup preserved pre-existing config.
  Assert-FileExists -Path (Join-Path $backupPath 'preexisting-user-config.txt') `
    -Reason 'backup of pre-existing Config file'

  # Verify DOF DLL payloads.
  Assert-FileExists -Path (Join-Path $paths.Root 'x64\DirectOutput.dll') -Reason 'x64 DLL copy'
  Assert-FileExists -Path (Join-Path $paths.Root 'x86\DirectOutput.dll') -Reason 'x86 DLL copy'

  # Verify baseline Config file.
  Assert-FileExists -Path (Join-Path $paths.Config 'GlobalConfig_B2SServer.xml') -Reason 'baseline Config copy'

  # Verify template overlay.
  Assert-FileExists -Path (Join-Path $paths.Config 'Cabinet.xml') -Reason 'template overlay (matrix_plus_3_strips)'
  $expectedCabinet = Get-Content -LiteralPath (Join-Path $repoRoot 'DOF\Config\templates\02-matrix-and-flasher-strips\Cabinet.xml') -Raw
  $actualCabinet   = Get-Content -LiteralPath (Join-Path $paths.Config 'Cabinet.xml') -Raw
  if (-not $actualCabinet.Equals($expectedCabinet, [System.StringComparison]::Ordinal)) {
    throw 'Template overlay verification failed: Cabinet.xml did not match expected template payload.'
  }

  # Verify app was installed.
  Assert-FileExists -Path (Join-Path $installDir 'VirtualDofMatrix.App.exe') -Reason 'app exe installed'

  # Verify release-manifest payload was installed.
  Assert-FileExists -Path (Join-Path $installDir 'instructions.html') -Reason 'release instructions installed'
  Assert-FileExists -Path (Join-Path $installDir 'DOF\x64\DirectOutput.dll') -Reason 'installed release DOF payload'

  # Verify no installer/uninstaller exe was copied into the app install folder or any subfolder.
  $installerCopiesInAppFolder = @(Get-ChildItem -LiteralPath $installDir -Filter 'VirtualDofMatrix.Installer.exe' -Recurse -File -ErrorAction SilentlyContinue)
  if ($installerCopiesInAppFolder.Count -gt 0) {
    $paths = ($installerCopiesInAppFolder | ForEach-Object { $_.FullName }) -join ', '
    throw "Installer exe should not be copied into the app install folder or subfolders. Found: $paths"
  }

  # Verify ARP support copy exists outside the app install folder.
  $programDataUninstaller = Join-Path $env:ProgramData 'VirtualDofMatrix\Uninstall\VirtualDofMatrix.Installer.exe'
  Assert-FileExists -Path $programDataUninstaller -Reason 'ARP uninstall support exe'

  # Verify ARP registry entry.
  $arpKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\VirtualDofMatrix'
  Assert-RegistryValueExists -KeyPath $arpKey -ValueName 'DisplayName'   -Reason 'ARP DisplayName'
  Assert-RegistryValueExists -KeyPath $arpKey -ValueName 'UninstallString' -Reason 'ARP UninstallString'
  Assert-RegistryValueExists -KeyPath $arpKey -ValueName 'InstallLocation' -Reason 'ARP InstallLocation'

  return $installDir
}

function Invoke-SilentUninstallAndValidate {
  param([string]$InstallerExePath, [string]$InstallDir)

  $uninstallLog = Join-Path $logRoot 'installer-silent-uninstall.log'
  Write-Host "Running silent uninstall from: $InstallerExePath"

  # Uninstall via the ARP support copy outside the app install folder.
  $uninstallerExe = Join-Path $env:ProgramData 'VirtualDofMatrix\Uninstall\VirtualDofMatrix.Installer.exe'
  if (-not (Test-Path -LiteralPath $uninstallerExe -PathType Leaf)) {
    # Fallback to original installer exe.
    $uninstallerExe = $InstallerExePath
  }

  $proc = Start-Process -FilePath $uninstallerExe -ArgumentList @('--uninstall', '--silent') `
    -PassThru -Wait -RedirectStandardOutput $uninstallLog -RedirectStandardError "$uninstallLog.err.log"

  if ($proc.ExitCode -ne 0) {
    if (Test-Path $uninstallLog) { Get-Content $uninstallLog | Write-Host }
    throw "Silent uninstall exited with code $($proc.ExitCode). See $uninstallLog."
  }

  # Verify ARP entry removed.
  $arpKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\VirtualDofMatrix'
  if (Test-Path -LiteralPath $arpKey) {
    throw "ARP registry key should have been removed by uninstall: $arpKey"
  }

  # Verify install directory removed (or at minimum the app exe gone).
  if (Test-Path -LiteralPath (Join-Path $InstallDir 'VirtualDofMatrix.App.exe') -PathType Leaf) {
    throw "App exe should have been removed by uninstall."
  }
}

if (-not (Test-Path -LiteralPath $InstallerPath -PathType Leaf)) {
  throw "Installer exe not found: $InstallerPath"
}

Write-Host 'Running silent install and validation...'
$installDir = Invoke-SilentInstallAndValidate -InstallerExePath (Resolve-Path $InstallerPath).Path

Write-Host 'Running silent uninstall and validation...'
Invoke-SilentUninstallAndValidate -InstallerExePath (Resolve-Path $InstallerPath).Path -InstallDir $installDir

Write-Host 'Installer integration checks completed successfully.'
