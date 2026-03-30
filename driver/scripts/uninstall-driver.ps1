param(
    [string]$PublishedNamePattern = "*VirtualDofMatrixVirtualSerial*.inf"
)

$ErrorActionPreference = "Stop"

$drivers = pnputil /enum-drivers | Out-String
$blocks = $drivers -split "`r?`n`r?`n"

foreach ($block in $blocks) {
    if ($block -like "*Published Name*" -and $block -like $PublishedNamePattern) {
        if ($block -match "Published Name\s*:\s*(\S+)") {
            $published = $matches[1]
            Write-Host "Removing $published"
            pnputil /delete-driver $published /uninstall /force
            if ($LASTEXITCODE -ne 0) {
                throw "pnputil delete failed for $published with exit code $LASTEXITCODE"
            }
        }
    }
}

pnputil /scan-devices
Write-Host "Driver uninstall/rescan completed."
