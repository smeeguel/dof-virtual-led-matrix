[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PublishOutput,

    [Parameter(Mandatory = $true)]
    [string]$ManifestPath,

    [Parameter(Mandatory = $true)]
    [string]$StagingRoot,

    [Parameter(Mandatory = $true)]
    [string]$AssetName
)

$ErrorActionPreference = 'Stop'

function Resolve-RepoPath {
    param([string]$Path)

    return [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../$Path"))
}

$repoRoot = Resolve-RepoPath "."
$publishDir = [System.IO.Path]::GetFullPath($PublishOutput)
$manifestFile = [System.IO.Path]::GetFullPath($ManifestPath)
$stagingDir = [System.IO.Path]::GetFullPath($StagingRoot)

if (-not (Test-Path -LiteralPath $publishDir -PathType Container)) {
    throw "Publish output directory not found: $publishDir"
}

if (-not (Test-Path -LiteralPath $manifestFile -PathType Leaf)) {
    throw "Manifest file not found: $manifestFile"
}

if (Test-Path -LiteralPath $stagingDir) {
    Remove-Item -LiteralPath $stagingDir -Recurse -Force
}
New-Item -ItemType Directory -Path $stagingDir | Out-Null

Write-Host "Staging directory initialized at $stagingDir"
Write-Host "Manifest is authoritative; only mapped files will be copied."

$manifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
if ($null -eq $manifest.mappings -or $manifest.mappings.Count -eq 0) {
    throw "Manifest contains no mappings: $manifestFile"
}

for ($i = 0; $i -lt $manifest.mappings.Count; $i++) {
    $mapping = $manifest.mappings[$i]

    if ([string]::IsNullOrWhiteSpace($mapping.type)) {
        throw "Mapping[$i] is missing required property 'type'."
    }

    if ([string]::IsNullOrWhiteSpace($mapping.from)) {
        throw "Mapping[$i] is missing required property 'from'."
    }

    if ([string]::IsNullOrWhiteSpace($mapping.to)) {
        throw "Mapping[$i] is missing required property 'to'."
    }

    $type = $mapping.type.ToLowerInvariant()

    switch ($type) {
        'file' {
            $sourceFile = Resolve-RepoPath $mapping.from
            if (-not (Test-Path -LiteralPath $sourceFile -PathType Leaf)) {
                throw "Mapping[$i] file source not found: '$($mapping.from)'"
            }

            $destFile = [System.IO.Path]::GetFullPath((Join-Path $stagingDir $mapping.to))
            $destParent = Split-Path -Parent $destFile
            New-Item -ItemType Directory -Path $destParent -Force | Out-Null

            Copy-Item -LiteralPath $sourceFile -Destination $destFile -Force
            Write-Host "Mapping[$i] file copied: $($mapping.from) -> $($mapping.to)"
        }

        'directory' {
            $sourceDir = Resolve-RepoPath $mapping.from
            if (-not (Test-Path -LiteralPath $sourceDir -PathType Container)) {
                throw "Mapping[$i] directory source not found: '$($mapping.from)'"
            }

            $destDir = [System.IO.Path]::GetFullPath((Join-Path $stagingDir $mapping.to))
            New-Item -ItemType Directory -Path $destDir -Force | Out-Null

            $include = @()
            if ($null -ne $mapping.include) {
                $include = @($mapping.include)
            }

            $exclude = @()
            if ($null -ne $mapping.exclude) {
                $exclude = @($mapping.exclude)
            }

            $fileCount = 0
            if ($include.Count -gt 0 -or $exclude.Count -gt 0) {
                $files = Get-ChildItem -Path $sourceDir -Recurse -File
                if ($include.Count -gt 0) {
                    $files = $files | Where-Object {
                        $relative = [System.IO.Path]::GetRelativePath($sourceDir, $_.FullName).Replace('\\', '/')
                        foreach ($pattern in $include) {
                            if ($relative -like $pattern) { return $true }
                        }
                        return $false
                    }
                }
                if ($exclude.Count -gt 0) {
                    $files = $files | Where-Object {
                        $relative = [System.IO.Path]::GetRelativePath($sourceDir, $_.FullName).Replace('\\', '/')
                        foreach ($pattern in $exclude) {
                            if ($relative -like $pattern) { return $false }
                        }
                        return $true
                    }
                }

                $files = @($files)
                if ($files.Count -eq 0) {
                    throw "Mapping[$i] directory patterns matched zero files: '$($mapping.from)'"
                }

                foreach ($file in $files) {
                    $relative = [System.IO.Path]::GetRelativePath($sourceDir, $file.FullName)
                    $destination = Join-Path $destDir $relative
                    $destinationParent = Split-Path -Parent $destination
                    New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
                    Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
                    $fileCount++
                }
            }
            else {
                Copy-Item -Path (Join-Path $sourceDir '*') -Destination $destDir -Recurse -Force
                $fileCount = (Get-ChildItem -Path $sourceDir -Recurse -File).Count
                if ($fileCount -eq 0) {
                    throw "Mapping[$i] source directory contains no files: '$($mapping.from)'"
                }
            }

            Write-Host "Mapping[$i] directory copied ($fileCount files): $($mapping.from) -> $($mapping.to)"
        }

        'glob' {
            $globPattern = $mapping.from
            $matches = @(Get-ChildItem -Path (Resolve-RepoPath '.') -Recurse -File | Where-Object {
                $relative = [System.IO.Path]::GetRelativePath($repoRoot, $_.FullName).Replace('\\', '/')
                return $relative -like $globPattern
            })

            if ($matches.Count -eq 0) {
                throw "Mapping[$i] glob matched zero files: '$($mapping.from)'"
            }

            $destDir = [System.IO.Path]::GetFullPath((Join-Path $stagingDir $mapping.to))
            New-Item -ItemType Directory -Path $destDir -Force | Out-Null

            foreach ($match in $matches) {
                $destFile = Join-Path $destDir $match.Name
                Copy-Item -LiteralPath $match.FullName -Destination $destFile -Force
            }

            Write-Host "Mapping[$i] glob copied ($($matches.Count) files): $($mapping.from) -> $($mapping.to)"
        }

        default {
            throw "Mapping[$i] has unsupported type '$($mapping.type)'. Supported types: file, directory, glob."
        }
    }
}

$artifactPath = [System.IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $stagingDir) $AssetName))
if (Test-Path -LiteralPath $artifactPath) {
    Remove-Item -LiteralPath $artifactPath -Force
}

Write-Host "Creating zip artifact at $artifactPath"
Compress-Archive -Path (Join-Path $stagingDir '*') -DestinationPath $artifactPath -CompressionLevel Optimal

"artifact_path=$artifactPath" | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
"staging_dir=$stagingDir" | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
