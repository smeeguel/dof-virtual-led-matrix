param(
  [Parameter(Mandatory = $true)]
  [string]$MsiPath
)

$ErrorActionPreference = 'Stop'

# Centralized paths keep logs/artifacts predictable for upload on CI failures.
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

function New-FakeDofRoot {
  param([string]$Name)

  $fakeRoot = Join-Path $env:RUNNER_TEMP ("fake-dof-{0}-{1}" -f $Name, [guid]::NewGuid().ToString('N'))
  $configPath = Join-Path $fakeRoot 'Config'
  New-Item -Path $configPath -ItemType Directory -Force | Out-Null

  # Seed a pre-existing Config file so backup behavior can prove we preserve pre-install state.
  Set-Content -LiteralPath (Join-Path $configPath 'preexisting-user-config.txt') -Value 'original-config-value' -Encoding ascii

  return @{ Root = $fakeRoot; Config = $configPath }
}

function Invoke-SilentInstallAndValidate {
  param([string]$InstallerPath)

  $paths = New-FakeDofRoot -Name 'silent'
  $backupPath = Join-Path $paths.Root 'Backups\ci-backup'
  $installLog = Join-Path $logRoot 'msi-silent-install.log'

  # Silent mode gives deterministic integration assertions for payload copy semantics.
  $arguments = @(
    '/i', "`"$InstallerPath`"",
    '/qn',
    '/norestart',
    '/l*v', "`"$installLog`"",
    "DOFROOTPATH=$($paths.Root)",
    "DOFCONFIGPATH=$($paths.Config)",
    'BACKUP_ENABLED=1',
    "BACKUP_PATH=$backupPath",
    'TOY_TEMPLATE=matrix_plus_3_strips'
  )

  $proc = Start-Process -FilePath 'msiexec.exe' -ArgumentList $arguments -PassThru -Wait
  if ($proc.ExitCode -ne 0) {
    throw "Silent installer run failed with exit code $($proc.ExitCode). See $installLog."
  }

  # Verify backup captured the pre-created Config contents before installer overwrite/copy actions.
  Assert-FileExists -Path (Join-Path $backupPath 'preexisting-user-config.txt') -Reason 'backup of pre-existing Config should exist'

  # Verify both architecture DLL payloads were copied into fake DOF root.
  Assert-FileExists -Path (Join-Path $paths.Root 'x64\DirectOutput.dll') -Reason 'x64 DLL copy'
  Assert-FileExists -Path (Join-Path $paths.Root 'x86\DirectOutput.dll') -Reason 'x86 DLL copy'

  # Verify baseline Config (non-template) payload files are copied.
  Assert-FileExists -Path (Join-Path $paths.Config 'GlobalConfig_B2SServer.xml') -Reason 'baseline Config copy'

  # Verify selected template overlay content landed in destination Config.
  Assert-FileExists -Path (Join-Path $paths.Config 'Cabinet.xml') -Reason 'template overlay copy (matrix_plus_3_strips)'
  $expectedCabinet = Get-Content -LiteralPath (Join-Path $repoRoot 'DOF\Config\templates\02-matrix-and-flasher-strips\Cabinet.xml') -Raw
  $actualCabinet = Get-Content -LiteralPath (Join-Path $paths.Config 'Cabinet.xml') -Raw
  if (-not $actualCabinet.Equals($expectedCabinet, [System.StringComparison]::Ordinal)) {
    throw 'Template overlay verification failed: Config\Cabinet.xml did not match selected template payload.'
  }
}

function Invoke-SilentUninstallIfPresent {
  param([string]$InstallerPath)

  $uninstallLog = Join-Path $logRoot 'msi-silent-uninstall.log'
  # Run uninstall between silent and UI checks so the UI flow always starts in first-install mode
  # (otherwise CI can land in maintenance dialogs and the scripted key sequence no longer matches).
  $arguments = @(
    '/x', "`"$InstallerPath`"",
    '/qn',
    '/norestart',
    '/l*v', "`"$uninstallLog`""
  )

  $proc = Start-Process -FilePath 'msiexec.exe' -ArgumentList $arguments -PassThru -Wait
  if ($proc.ExitCode -ne 0) {
    throw "Silent uninstall run failed with exit code $($proc.ExitCode). See $uninstallLog."
  }
}

if (-not (Test-Path -LiteralPath $MsiPath -PathType Leaf)) {
  throw "MSI path not found: $MsiPath"
}

Invoke-SilentInstallAndValidate -InstallerPath (Resolve-Path $MsiPath).Path
Invoke-SilentUninstallIfPresent -InstallerPath (Resolve-Path $MsiPath).Path
Write-Host 'Skipping interactive MSI UI smoke checks in cloud CI; running deterministic silent checks only.'

Write-Host 'MSI integration checks completed successfully.'
