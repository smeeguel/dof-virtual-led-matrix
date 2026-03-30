param(
    [switch]$Repair,
    [string]$DriverInfPath = "../../driver/VirtualDofMatrixVirtualSerial/inf/VirtualDofMatrixVirtualSerial.inf",
    [string]$DriverSysPath = "",
    [string]$ServiceExePath = "../../src/VirtualDofMatrix.Service/bin/Release/net8.0-windows/VirtualDofMatrix.Service.exe",
    [string]$ServiceName = "VirtualDofMatrixProvisioning",
    [string]$LabCertSubject = "CN=VirtualDofMatrix Test Driver",
    [string]$LabCertPassword = "VirtualDofMatrix-TestCert-Only",
    [string]$Inf2CatOs = "10_X64",
    [switch]$DisableAutoLabSigning
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

function Resolve-ToolPath {
    param(
        [Parameter(Mandatory = $true)][string]$ExecutableName
    )

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

function Ensure-TestSigningMode {
    $bcdOutput = bcdedit /enum
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to query boot configuration using bcdedit."
    }

    if ($bcdOutput -match '(?im)^\s*testsigning\s+Yes\s*$') {
        Write-Host "BCDEdit testsigning already enabled."
        return
    }

    Write-Warning "BCDEdit testsigning is OFF. Enabling it for lab test-signed driver installs."
    bcdedit /set testsigning on
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to enable testsigning mode via bcdedit."
    }

    Write-Warning "Testsigning was enabled. A reboot is required before Windows accepts test-signed kernel drivers."
}

function Ensure-LabSigningCertificate {
    param(
        [Parameter(Mandatory = $true)][string]$Subject
    )

    $existing = Get-ChildItem -Path Cert:\LocalMachine\My |
        Where-Object { $_.Subject -eq $Subject -and $_.NotAfter -gt (Get-Date) } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1

    if ($null -ne $existing) {
        Write-Host "Using existing lab signing certificate: $($existing.Thumbprint)"
        return $existing
    }

    Write-Host "Creating new lab code-signing certificate: $Subject"
    return New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $Subject `
        -CertStoreLocation "Cert:\LocalMachine\My" `
        -HashAlgorithm "SHA256"
}

function Ensure-CertificateTrusted {
    param(
        [Parameter(Mandatory = $true)]$Certificate
    )

    $thumbprint = $Certificate.Thumbprint
    $alreadyInRoot = Get-ChildItem -Path Cert:\LocalMachine\Root | Where-Object { $_.Thumbprint -eq $thumbprint } | Select-Object -First 1
    if ($null -eq $alreadyInRoot) {
        Write-Host "Adding lab cert to LocalMachine\\Root"
        $null = New-Item -Path "Cert:\LocalMachine\Root\$thumbprint" -Value $Certificate -ErrorAction Stop
    }

    $alreadyInPublisher = Get-ChildItem -Path Cert:\LocalMachine\TrustedPublisher | Where-Object { $_.Thumbprint -eq $thumbprint } | Select-Object -First 1
    if ($null -eq $alreadyInPublisher) {
        Write-Host "Adding lab cert to LocalMachine\\TrustedPublisher"
        $null = New-Item -Path "Cert:\LocalMachine\TrustedPublisher\$thumbprint" -Value $Certificate -ErrorAction Stop
    }
}

function Ensure-DriverCatalog {
    param(
        [Parameter(Mandatory = $true)][string]$InfFilePath,
        [Parameter(Mandatory = $true)][string]$SysFilePath,
        [Parameter(Mandatory = $true)][string]$Inf2CatOsValue
    )

    $resolvedCat = Resolve-DriverCatalogPath -InfFilePath $InfFilePath -SysFilePath $SysFilePath
    if (-not [string]::IsNullOrWhiteSpace($resolvedCat) -and (Test-Path $resolvedCat)) {
        return $resolvedCat
    }

    $infDir = Split-Path -Parent $InfFilePath
    $infFileName = Split-Path -Leaf $InfFilePath
    $inf2catPath = Resolve-ToolPath -ExecutableName "Inf2Cat.exe"

    Write-Warning "No catalog found. Generating CAT with Inf2Cat for OS token '$Inf2CatOsValue'."
    & $inf2catPath /driver:$infDir /os:$Inf2CatOsValue /verbose
    if ($LASTEXITCODE -ne 0) {
        throw "Inf2Cat failed with exit code $LASTEXITCODE. Verify the INF is valid for OS token '$Inf2CatOsValue'."
    }

    $resolvedCat = Resolve-DriverCatalogPath -InfFilePath (Join-Path $infDir $infFileName) -SysFilePath $SysFilePath
    if ([string]::IsNullOrWhiteSpace($resolvedCat) -or -not (Test-Path $resolvedCat)) {
        throw "Inf2Cat completed but no catalog was found for INF '$InfFilePath'."
    }

    return $resolvedCat
}

function Ensure-TestSignedDriverPackage {
    param(
        [Parameter(Mandatory = $true)][string]$InfFilePath,
        [Parameter(Mandatory = $true)][string]$SysFilePath,
        [Parameter(Mandatory = $true)][string]$CertSubject,
        [Parameter(Mandatory = $true)][string]$CertPassword,
        [Parameter(Mandatory = $true)][string]$Inf2CatOsValue
    )

    Ensure-TestSigningMode
    $cert = Ensure-LabSigningCertificate -Subject $CertSubject
    Ensure-CertificateTrusted -Certificate $cert

    $resolvedCat = Ensure-DriverCatalog -InfFilePath $InfFilePath -SysFilePath $SysFilePath -Inf2CatOsValue $Inf2CatOsValue
    $catSignature = Get-AuthenticodeSignature -FilePath $resolvedCat
    if ($catSignature.Status -eq "Valid") {
        Write-Host "Catalog signature is already valid: $resolvedCat"
        return $resolvedCat
    }

    $signtoolPath = Resolve-ToolPath -ExecutableName "signtool.exe"
    $tempPfxPath = Join-Path $env:TEMP ("vdm-test-signing-" + [Guid]::NewGuid() + ".pfx")
    $securePassword = ConvertTo-SecureString $CertPassword -AsPlainText -Force
    Export-PfxCertificate -Cert $cert -FilePath $tempPfxPath -Password $securePassword | Out-Null

    try {
        Write-Host "Signing SYS and CAT with lab certificate."
        & $signtoolPath sign /fd SHA256 /v /f $tempPfxPath /p $CertPassword "$SysFilePath"
        if ($LASTEXITCODE -ne 0) {
            throw "signtool failed while signing SYS with exit code $LASTEXITCODE."
        }

        & $signtoolPath sign /fd SHA256 /v /f $tempPfxPath /p $CertPassword "$resolvedCat"
        if ($LASTEXITCODE -ne 0) {
            throw "signtool failed while signing CAT with exit code $LASTEXITCODE."
        }
    } finally {
        Remove-Item -Path $tempPfxPath -ErrorAction SilentlyContinue
    }

    $catSignature = Get-AuthenticodeSignature -FilePath $resolvedCat
    if ($catSignature.Status -ne "Valid") {
        throw "Catalog signature is not valid after signing. Status: $($catSignature.Status)"
    }

    return $resolvedCat
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

if (-not $DisableAutoLabSigning) {
    $resolvedCat = Ensure-TestSignedDriverPackage `
        -InfFilePath $resolvedInf `
        -SysFilePath $resolvedSys `
        -CertSubject $LabCertSubject `
        -CertPassword $LabCertPassword `
        -Inf2CatOsValue $Inf2CatOs
} else {
    $resolvedCat = Resolve-DriverCatalogPath -InfFilePath $resolvedInf -SysFilePath $resolvedSys
}

$stagingDir = Join-Path $env:TEMP ("vdm-driver-staging-" + [Guid]::NewGuid())
New-Item -ItemType Directory -Path $stagingDir | Out-Null
Copy-Item $resolvedInf (Join-Path $stagingDir $infFileName)
Copy-Item $resolvedSys (Join-Path $stagingDir $sysFileName)

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
