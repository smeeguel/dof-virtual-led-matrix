param(
    [switch]$Repair,
    [string]$DriverInfPath = "../../driver/VirtualDofMatrixVirtualSerial/inf/VirtualDofMatrixVirtualSerial.inf",
    [string]$DriverSysPath = "",
    [string]$DriverCatPath = "",
    [string]$ServiceExePath = "../../src/VirtualDofMatrix.Service/bin/Release/net8.0-windows/VirtualDofMatrix.Service.exe",
    [string]$ServiceName = "VirtualDofMatrixProvisioning"
)

$ErrorActionPreference = "Stop"

function Assert-Admin {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) {
        throw "Installer must run elevated as Administrator."
    }
}

function Get-DriverSysFromInf {
    param([string]$InfFile)

    $sysLine = Get-Content $InfFile |
        Where-Object { $_ -match '\.sys\s*$' -and $_ -notmatch '^\s*;' } |
        Select-Object -First 1

    if ([string]::IsNullOrWhiteSpace($sysLine)) {
        throw "Unable to locate .sys reference inside INF: $InfFile"
    }

    $sysToken = ($sysLine -split '[,\s]+' | Where-Object { $_ -match '\.sys$' } | Select-Object -First 1)
    $sysToken = $sysToken.Trim('"')
    return (Split-Path $sysToken -Leaf)
}

function Resolve-DriverCatalogPath {
    param(
        [string]$InfFilePath,
        [string]$SysFilePath,
        [string]$ExplicitCatPath
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitCatPath)) {
        return (Resolve-Path $ExplicitCatPath).Path
    }

    $catFileName = [System.IO.Path]::GetFileNameWithoutExtension($InfFilePath) + ".cat"
    $searchRoots = @(
        (Split-Path -Parent $InfFilePath),
        (Split-Path -Parent $SysFilePath)
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique

    foreach ($root in $searchRoots) {
        $candidate = Join-Path $root $catFileName
        if (Test-Path $candidate) {
            return (Resolve-Path $candidate).Path
        }
    }

    return $null
}

Assert-Admin

$resolvedInf = (Resolve-Path $DriverInfPath).Path
$infDirectory = Split-Path -Parent $resolvedInf
$infFileName = Split-Path -Leaf $resolvedInf
$sysFileName = Get-DriverSysFromInf -InfFile $resolvedInf

$resolvedSys = $null
if (-not [string]::IsNullOrWhiteSpace($DriverSysPath)) {
    $resolvedSys = (Resolve-Path $DriverSysPath).Path
} else {
    $candidateInInfDir = Join-Path $infDirectory $sysFileName
    if (Test-Path $candidateInInfDir) {
        $resolvedSys = (Resolve-Path $candidateInInfDir).Path
    } else {
        $repoRoot = Resolve-Path (Join-Path $PSScriptRoot "../..")
        $candidateBuilt = Get-ChildItem -Path $repoRoot -Recurse -Filter $sysFileName -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTimeUtc -Descending |
            Select-Object -First 1

        if ($null -ne $candidateBuilt) {
            $resolvedSys = $candidateBuilt.FullName
        }
    }
}

if ([string]::IsNullOrWhiteSpace($resolvedSys) -or -not (Test-Path $resolvedSys)) {
    throw "Driver binary '$sysFileName' was not found. Build the KMDF driver first or pass -DriverSysPath explicitly."
}

$resolvedCat = Resolve-DriverCatalogPath -InfFilePath $resolvedInf -SysFilePath $resolvedSys -ExplicitCatPath $DriverCatPath
if ([string]::IsNullOrWhiteSpace($resolvedCat) -or -not (Test-Path $resolvedCat)) {
    throw @"
A signed catalog (.cat) was not found.

Release install mode requires a production-signed driver package with INF + SYS + CAT.
Actions:
  1) Build/package the driver so INF/SYS/CAT are generated together.
  2) Submit and sign through Microsoft release/attestation workflow.
  3) Re-run install.ps1 and optionally pass -DriverCatPath explicitly.
"@
}

Write-Host "Running release signature preflight checks"
& "$PSScriptRoot/verify-release-driver-signature.ps1" -DriverInfPath $resolvedInf -DriverSysPath $resolvedSys -DriverCatPath $resolvedCat

$stagingDir = Join-Path $env:TEMP ("vdm-driver-staging-" + [Guid]::NewGuid())
New-Item -ItemType Directory -Path $stagingDir | Out-Null
Copy-Item $resolvedInf (Join-Path $stagingDir $infFileName)
Copy-Item $resolvedSys (Join-Path $stagingDir $sysFileName)
Copy-Item $resolvedCat (Join-Path $stagingDir (Split-Path -Leaf $resolvedCat))

$stagedInf = Join-Path $stagingDir $infFileName
Write-Host "Installing driver package from staged folder: $stagingDir"
Write-Host "INF: $stagedInf"
Write-Host "SYS: $(Join-Path $stagingDir $sysFileName)"
Write-Host "CAT: $(Join-Path $stagingDir (Split-Path -Leaf $resolvedCat))"
pnputil /add-driver "$stagedInf" /install
if ($LASTEXITCODE -ne 0) {
    if ($LASTEXITCODE -eq -536870353) {
        throw @"
Driver installation failed with exit code $LASTEXITCODE (signature trust failure).

Release mode does not enable test-signing and does not modify Secure Boot policy.
Install a production-signed package trusted by this machine and retry.
"@
    }

    throw "Driver installation failed with exit code $LASTEXITCODE"
}

$resolvedServiceExe = Resolve-Path $ServiceExePath
if ($Repair) {
    Write-Host "Repair mode: ensuring service configuration for $ServiceName"
} elseif ((Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) -eq $null) {
    Write-Host "Creating Windows service: $ServiceName"
    sc.exe create $ServiceName binPath= "`"$resolvedServiceExe`"" start= auto obj= LocalSystem
    if ($LASTEXITCODE -ne 0) { throw "Service creation failed with exit code $LASTEXITCODE" }
}

Write-Host "Configuring service recovery options"
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/15000
if ($LASTEXITCODE -ne 0) { throw "Failed to set service recovery options" }

sc.exe failureflag $ServiceName 1
if ($LASTEXITCODE -ne 0) { throw "Failed to enable failure actions flag" }

Write-Host "Starting service"
sc.exe start $ServiceName | Out-Null

Write-Host "Running post-install health check"
& "$PSScriptRoot/health-check.ps1" -ServiceName $ServiceName

Write-Host "Install completed successfully."
