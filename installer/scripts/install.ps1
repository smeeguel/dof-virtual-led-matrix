param(
    [switch]$Repair,
    [string]$DriverInfPath = "../../driver/VirtualDofMatrixVirtualSerial/inf/VirtualDofMatrixVirtualSerial.inf",
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

Assert-Admin

$resolvedInf = Resolve-Path $DriverInfPath
Write-Host "Installing signed driver package: $resolvedInf"
pnputil /add-driver "$resolvedInf" /install
if ($LASTEXITCODE -ne 0) { throw "Driver installation failed with exit code $LASTEXITCODE" }

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
