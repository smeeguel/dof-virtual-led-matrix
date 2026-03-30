param(
    [string]$InfPath = "../VirtualDofMatrixVirtualSerial/inf/VirtualDofMatrixVirtualSerial.inf"
)

$ErrorActionPreference = "Stop"

$resolvedInf = Resolve-Path $InfPath
Write-Host "Installing driver from $resolvedInf"

pnputil /add-driver "$resolvedInf" /install
if ($LASTEXITCODE -ne 0) {
    throw "pnputil install failed with exit code $LASTEXITCODE"
}

Write-Host "Driver install completed."
