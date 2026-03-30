param(
    [switch]$Repair,
    [string]$DriverInfPath = "../../driver/VirtualDofMatrixVirtualSerial/inf/VirtualDofMatrixVirtualSerial.inf",
    [string]$DriverSysPath = "",
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
        [string]$SysFilePath
    )

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

    foreach ($root in $searchRoots) {
        $fallback = Get-ChildItem -Path $root -Filter "*.cat" -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTimeUtc -Descending |
            Select-Object -First 1
        if ($null -ne $fallback) {
            return $fallback.FullName
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

$stagingDir = Join-Path $env:TEMP ("vdm-driver-staging-" + [Guid]::NewGuid())
New-Item -ItemType Directory -Path $stagingDir | Out-Null
Copy-Item $resolvedInf (Join-Path $stagingDir $infFileName)
Copy-Item $resolvedSys (Join-Path $stagingDir $sysFileName)

$resolvedCat = Resolve-DriverCatalogPath -InfFilePath $resolvedInf -SysFilePath $resolvedSys
if (-not [string]::IsNullOrWhiteSpace($resolvedCat) -and (Test-Path $resolvedCat)) {
    Copy-Item $resolvedCat (Join-Path $stagingDir (Split-Path -Leaf $resolvedCat))
    Write-Host "CAT: $(Join-Path $stagingDir (Split-Path -Leaf $resolvedCat))"
} else {
    Write-Warning "No catalog (.cat) was found next to INF or SYS. Unsigned/test builds will fail on normal Windows policy."
}

$stagedInf = Join-Path $stagingDir $infFileName
Write-Host "Installing driver package from staged folder: $stagingDir"
Write-Host "INF: $stagedInf"
Write-Host "SYS: $(Join-Path $stagingDir $sysFileName)"
pnputil /add-driver "$stagedInf" /install
if ($LASTEXITCODE -ne 0) {
    if ($LASTEXITCODE -eq -536870353) {
        throw @"
Driver installation failed with exit code $LASTEXITCODE (unsigned package).

Windows rejected this INF because no trusted digital signature metadata was found.
Actions:
  1) Build/package the driver so INF/SYS/CAT are generated together.
  2) Ensure the CAT is signed (attestation/production for normal systems).
  3) For lab-only testing, use a test-signed package + test-signing mode.
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
