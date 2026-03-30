param(
    [Parameter(Mandatory = $true)][string]$DriverInfPath,
    [Parameter(Mandatory = $true)][string]$DriverSysPath,
    [Parameter(Mandatory = $true)][string]$DriverCatPath
)

$ErrorActionPreference = "Stop"

function Resolve-ToolPath {
    param([Parameter(Mandatory = $true)][string]$ExecutableName)

    $tool = Get-Command $ExecutableName -ErrorAction SilentlyContinue
    if ($null -ne $tool) {
        return $tool.Source
    }

    $kitsRoot = "C:\Program Files (x86)\Windows Kits\10\bin"
    if (Test-Path $kitsRoot) {
        $candidate = Get-ChildItem -Path $kitsRoot -Recurse -Filter $ExecutableName -File -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($null -ne $candidate) {
            return $candidate.FullName
        }
    }

    throw "Required tool '$ExecutableName' was not found. Install Windows SDK/WDK developer tools and retry."
}

$resolvedInf = (Resolve-Path $DriverInfPath).Path
$resolvedSys = (Resolve-Path $DriverSysPath).Path
$resolvedCat = (Resolve-Path $DriverCatPath).Path

if (-not (Test-Path $resolvedInf) -or -not (Test-Path $resolvedSys) -or -not (Test-Path $resolvedCat)) {
    throw "Release signature preflight failed: INF/SYS/CAT must all exist."
}

$catSig = Get-AuthenticodeSignature -FilePath $resolvedCat
if ($catSig.Status -ne "Valid") {
    throw "Catalog signature is not valid: $resolvedCat (status: $($catSig.Status))."
}

$sysSig = Get-AuthenticodeSignature -FilePath $resolvedSys
if ($sysSig.Status -ne "Valid") {
    throw "Driver binary signature is not valid: $resolvedSys (status: $($sysSig.Status))."
}

$signtoolPath = Resolve-ToolPath -ExecutableName "signtool.exe"
& $signtoolPath verify /kp /v "$resolvedCat"
if ($LASTEXITCODE -ne 0) {
    throw "signtool /kp verification failed for CAT: $resolvedCat"
}

& $signtoolPath verify /kp /v "$resolvedSys"
if ($LASTEXITCODE -ne 0) {
    throw "signtool /kp verification failed for SYS: $resolvedSys"
}

Write-Host "Release signature preflight checks passed."
Write-Host "INF: $resolvedInf"
Write-Host "SYS: $resolvedSys"
Write-Host "CAT: $resolvedCat"
