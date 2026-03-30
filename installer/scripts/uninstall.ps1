param(
    [switch]$RemoveActivePairs,
    [string]$ServiceName = "VirtualDofMatrixProvisioning",
    [string]$PipeName = "VirtualDofMatrix.Provisioning.v1",
    [string]$DriverPublishedNamePattern = "*VirtualDofMatrixVirtualSerial*.inf"
)

$ErrorActionPreference = "Stop"

function Remove-Pairs {
    param([string]$PipeName)

    $client = New-Object System.IO.Pipes.NamedPipeClientStream('.', $PipeName, [System.IO.Pipes.PipeDirection]::InOut)
    $client.Connect(3000)

    $writer = New-Object System.IO.StreamWriter($client)
    $writer.AutoFlush = $true
    $reader = New-Object System.IO.StreamReader($client)

    $writer.WriteLine('{"command":"list","txPort":null,"rxPort":null}')
    $responseLine = $reader.ReadLine()
    if ([string]::IsNullOrWhiteSpace($responseLine)) {
        throw "No list response from service while removing pairs."
    }

    $response = $responseLine | ConvertFrom-Json
    if ($response.success -and $response.data) {
        foreach ($pair in $response.data) {
            $deletePayload = "{\"command\":\"delete\",\"txPort\":\"$($pair.txPort)\",\"rxPort\":\"$($pair.rxPort)\"}"
            $writer.WriteLine($deletePayload)
            [void]$reader.ReadLine()
        }
    }

    $reader.Dispose()
    $writer.Dispose()
    $client.Dispose()
}

if ($RemoveActivePairs) {
    Write-Host "Attempting to remove active pairs before uninstall"
    try {
        Remove-Pairs -PipeName $PipeName
    } catch {
        Write-Warning "Failed to remove active pairs: $($_.Exception.Message)"
    }
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($null -ne $existing) {
    Write-Host "Stopping service $ServiceName"
    sc.exe stop $ServiceName | Out-Null
    Start-Sleep -Seconds 2

    Write-Host "Deleting service $ServiceName"
    sc.exe delete $ServiceName | Out-Null
}

Write-Host "Uninstalling driver package(s)"
$drivers = pnputil /enum-drivers | Out-String
$blocks = $drivers -split "`r?`n`r?`n"

foreach ($block in $blocks) {
    if ($block -like "*Published Name*" -and $block -like $DriverPublishedNamePattern) {
        if ($block -match "Published Name\s*:\s*(\S+)") {
            $published = $matches[1]
            pnputil /delete-driver $published /uninstall /force
        }
    }
}

pnputil /scan-devices | Out-Null
Write-Host "Uninstall completed."
