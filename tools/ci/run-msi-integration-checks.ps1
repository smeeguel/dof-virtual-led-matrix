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

function Wait-InstallerWindow {
  param(
    [System.Diagnostics.Process]$Process,
    [int]$TimeoutSeconds = 60
  )

  $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
  do {
    $Process.Refresh()
    if ($Process.MainWindowHandle -ne 0) {
      return $true
    }

    Start-Sleep -Milliseconds 250
  } while ((Get-Date) -lt $deadline -and -not $Process.HasExited)

  return $false
}

function Send-InstallerKeys {
  param(
    [System.Diagnostics.Process]$Process,
    [string]$WindowTitle,
    [string]$Keys,
    [int]$DelayMs = 1200,
    [int]$ActivateTimeoutSeconds = 20
  )

  $shell = New-Object -ComObject WScript.Shell
  $deadline = (Get-Date).AddSeconds($ActivateTimeoutSeconds)
  $activated = $false
  $attempt = 0
  do {
    $attempt++
    $Process.Refresh()
    $mainWindowTitle = $Process.MainWindowTitle
    $mainWindowHandle = $Process.MainWindowHandle

    # Prefer process-id activation so CI does not depend on localized or timing-sensitive window captions.
    $activated = $shell.AppActivate($Process.Id)
    if (-not $activated -and -not [string]::IsNullOrWhiteSpace($WindowTitle)) {
      # Keep a title fallback for edge cases where msiexec surfaces a child dialog in another top-level window.
      $activated = $shell.AppActivate($WindowTitle)
    }

    if ($activated) {
      break
    }

    Write-Host ("AppActivate retry {0} for key '{1}' (PID={2}, Handle={3}, Title='{4}')." -f
      $attempt, $Keys, $Process.Id, $mainWindowHandle, $mainWindowTitle)
    Start-Sleep -Milliseconds 350
  } while ((Get-Date) -lt $deadline -and -not $Process.HasExited)

  if (-not $activated) {
    throw ("Could not activate installer window for key sequence '{0}'. PID={1}, LastKnownTitle='{2}', LastKnownHandle={3}" -f
      $Keys, $Process.Id, $Process.MainWindowTitle, $Process.MainWindowHandle)
  }

  Start-Sleep -Milliseconds 300
  Write-Host ("Sending key sequence '{0}' to installer PID {1}." -f $Keys, $Process.Id)
  $shell.SendKeys($Keys)
  Start-Sleep -Milliseconds $DelayMs
}

function Invoke-UiSmokeRun {
  param([string]$InstallerPath)

  $paths = New-FakeDofRoot -Name 'ui'
  $uiLog = Join-Path $logRoot 'msi-ui-smoke.log'

  # Full UI mode + SendKeys is a lightweight smoke check that the dialog path remains wired in CI.
  $arguments = @(
    '/i', "`"$InstallerPath`"",
    '/l*v', "`"$uiLog`"",
    "DOFROOTPATH=$($paths.Root)",
    "DOFCONFIGPATH=$($paths.Config)",
    'BACKUP_ENABLED=0',
    'TOY_TEMPLATE=single_matrix',
    # Keep CI deterministic: do not launch the app from ExitDialog because it can keep msiexec alive.
    'WIXUI_EXITDIALOGOPTIONALCHECKBOX=0'
  )

  $proc = Start-Process -FilePath 'msiexec.exe' -ArgumentList $arguments -PassThru

  try {
    if (-not (Wait-InstallerWindow -Process $proc -TimeoutSeconds 90)) {
      throw 'Installer UI window never appeared for smoke run.'
    }

    # Walk through: EULA -> DOF check -> InstallDir -> Template -> Summary -> Install -> Exit.
    Send-InstallerKeys -Process $proc -WindowTitle 'Virtual DOF Matrix Setup' -Keys '%a' -DelayMs 800
    Send-InstallerKeys -Process $proc -WindowTitle 'Virtual DOF Matrix Setup' -Keys '%n'
    Send-InstallerKeys -Process $proc -WindowTitle 'Virtual DOF Matrix Setup' -Keys '%n'
    Send-InstallerKeys -Process $proc -WindowTitle 'Virtual DOF Matrix Setup' -Keys '%n'
    Send-InstallerKeys -Process $proc -WindowTitle 'Virtual DOF Matrix Setup' -Keys '%n'
    Send-InstallerKeys -Process $proc -WindowTitle 'Virtual DOF Matrix Setup' -Keys '%i' -DelayMs 2000

    # Allow install progress to complete before exiting final dialog.
    Start-Sleep -Seconds 8
    Send-InstallerKeys -Process $proc -WindowTitle 'Virtual DOF Matrix Setup' -Keys '%f' -DelayMs 1000

    $null = $proc.WaitForExit(120000)
    if (-not $proc.HasExited) {
      throw 'Installer UI smoke run timed out waiting for process exit.'
    }

    if ($proc.ExitCode -ne 0) {
      throw "Installer UI smoke run failed with exit code $($proc.ExitCode). See $uiLog."
    }

    # Validate that each expected UI dialog was reached at least once in sequence log output.
    $logText = Get-Content -LiteralPath $uiLog -Raw
    foreach ($dialogId in @('LicenseAgreementDlg', 'DofCheckDlg', 'InstallDirDlg', 'ToyTemplateDlg', 'VerifyReadyDlg', 'ProgressDlg', 'ExitDialog')) {
      if ($logText -notmatch [regex]::Escape($dialogId)) {
        throw "UI smoke log did not include expected dialog '$dialogId'."
      }
    }
  }
  finally {
    if (-not $proc.HasExited) {
      $proc | Stop-Process -Force
    }
  }
}

if (-not (Test-Path -LiteralPath $MsiPath -PathType Leaf)) {
  throw "MSI path not found: $MsiPath"
}

Invoke-SilentInstallAndValidate -InstallerPath (Resolve-Path $MsiPath).Path
Invoke-SilentUninstallIfPresent -InstallerPath (Resolve-Path $MsiPath).Path
Invoke-UiSmokeRun -InstallerPath (Resolve-Path $MsiPath).Path

Write-Host 'MSI integration checks completed successfully.'
