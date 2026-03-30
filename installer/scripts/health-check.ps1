param(
    [string]$ServiceName = "VirtualDofMatrixProvisioning",
    [string]$PipeName = "VirtualDofMatrix.Provisioning.v1"
)

$ErrorActionPreference = "Stop"

$svc = Get-Service -Name $ServiceName -ErrorAction Stop
if ($svc.Status -ne "Running") {
    throw "Service '$ServiceName' is not running (status: $($svc.Status))."
}

$client = New-Object System.IO.Pipes.NamedPipeClientStream('.', $PipeName, [System.IO.Pipes.PipeDirection]::InOut)
$client.Connect(3000)

$writer = New-Object System.IO.StreamWriter($client)
$writer.AutoFlush = $true
$reader = New-Object System.IO.StreamReader($client)

$request = '{"command":"health","txPort":null,"rxPort":null}'
$writer.WriteLine($request)

$responseLine = $reader.ReadLine()
if ([string]::IsNullOrWhiteSpace($responseLine)) {
    throw "Health check failed: no response from provisioning pipe."
}

$response = $responseLine | ConvertFrom-Json
if (-not $response.success) {
    throw "Health check failed: $($response.errorCode) - $($response.message)"
}

Write-Host "Health check OK: $responseLine"

$reader.Dispose()
$writer.Dispose()
$client.Dispose()
