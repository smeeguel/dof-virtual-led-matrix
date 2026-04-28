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

function Invoke-InstallerAuthoringGuardrails {
  # Guardrails validate launch wiring semantics that are difficult to exercise in headless CI UI automation.
  $productWxsPath = Join-Path $repoRoot 'installer\VirtualDofMatrix.Setup\Product.wxs'
  $bundleWxsPath = Join-Path $repoRoot 'installer\VirtualDofMatrix.Setup\Bundle.wxs'
  Assert-FileExists -Path $productWxsPath -Reason 'installer authoring source'
  Assert-FileExists -Path $bundleWxsPath -Reason 'bundle authoring source'

  [xml]$productXml = Get-Content -LiteralPath $productWxsPath -Raw
  [xml]$bundleXml = Get-Content -LiteralPath $bundleWxsPath -Raw
  $ns = New-Object System.Xml.XmlNamespaceManager($productXml.NameTable)
  $ns.AddNamespace('wix', 'http://wixtoolset.org/schemas/v4/wxs')

  # Verify app launch target resolves from INSTALLFOLDER into the installed EXE path.
  $appLaunchProperty = $productXml.SelectSingleNode("//wix:SetProperty[@Id='APPLAUNCHTARGET']", $ns)
  if ($null -eq $appLaunchProperty) {
    throw 'Missing SetProperty Id=APPLAUNCHTARGET in Product.wxs.'
  }
  if ($appLaunchProperty.GetAttribute('Value') -ne '[INSTALLFOLDER]VirtualDofMatrix.App.exe') {
    throw "Unexpected APPLAUNCHTARGET value: '$($appLaunchProperty.GetAttribute('Value'))'."
  }

  # Verify DOF URL click cannot clobber app launch target by requiring dedicated URL-target wiring.
  $dofTargetCA = $productXml.SelectSingleNode("//wix:CustomAction[@Id='SetWixShellExecTargetForDofUrl']", $ns)
  if ($null -eq $dofTargetCA) {
    throw 'Missing CustomAction SetWixShellExecTargetForDofUrl in Product.wxs.'
  }
  if ($dofTargetCA.GetAttribute('Value') -ne '[DofDownloadTarget]') {
    throw "Unexpected DOF URL shell target value: '$($dofTargetCA.GetAttribute('Value'))'."
  }

  # Verify first-install Finish launches exactly once via ExitDialog and sets target immediately before launch.
  $setAppTargetPublish = $productXml.SelectSingleNode("//wix:Publish[@Dialog='ExitDialog' and @Control='Finish' and @Value='SetWixShellExecTargetForApp']", $ns)
  $launchPublish = $productXml.SelectSingleNode("//wix:Publish[@Dialog='ExitDialog' and @Control='Finish' and @Value='LaunchInstalledApp']", $ns)
  if ($null -eq $setAppTargetPublish -or $null -eq $launchPublish) {
    throw 'ExitDialog Finish must publish SetWixShellExecTargetForApp and LaunchInstalledApp.'
  }
  if ([int]$setAppTargetPublish.GetAttribute('Order') -ge [int]$launchPublish.GetAttribute('Order')) {
    throw 'SetWixShellExecTargetForApp must run before LaunchInstalledApp on ExitDialog Finish.'
  }
  if ($launchPublish.GetAttribute('Condition') -notmatch 'NOT Installed') {
    throw 'LaunchInstalledApp publish condition must remain first-install only (NOT Installed).'
  }
  $launchPublishCount = $productXml.SelectNodes("//wix:Publish[@Value='LaunchInstalledApp']", $ns).Count
  if ($launchPublishCount -ne 1) {
    throw "Expected exactly one LaunchInstalledApp publish event, found $launchPublishCount."
  }

  # Ensure Burn no longer defines BA-level launch target that could duplicate app starts.
  $bundleNs = New-Object System.Xml.XmlNamespaceManager($bundleXml.NameTable)
  $bundleNs.AddNamespace('wix', 'http://wixtoolset.org/schemas/v4/wxs')
  $bundleNs.AddNamespace('bal', 'http://wixtoolset.org/schemas/v4/wxs/bal')
  $baNode = $bundleXml.SelectSingleNode('//wix:BootstrapperApplication/bal:WixStandardBootstrapperApplication', $bundleNs)
  if ($null -eq $baNode) {
    throw 'Missing WixStandardBootstrapperApplication node in Bundle.wxs.'
  }
  if ($baNode.Attributes['LaunchTarget']) {
    throw 'Bundle.wxs must not define BA LaunchTarget when MSI ExitDialog launch is authoritative.'
  }
}

if (-not (Test-Path -LiteralPath $MsiPath -PathType Leaf)) {
  throw "MSI path not found: $MsiPath"
}

Invoke-SilentInstallAndValidate -InstallerPath (Resolve-Path $MsiPath).Path
Invoke-SilentUninstallIfPresent -InstallerPath (Resolve-Path $MsiPath).Path
Invoke-InstallerAuthoringGuardrails
Write-Host 'Skipping interactive MSI UI smoke checks in cloud CI; running deterministic silent checks only.'

Write-Host 'MSI integration checks completed successfully.'
