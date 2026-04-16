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

function Resolve-MappingSourcePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [ValidateSet('Leaf', 'Container')]
        [string]$PathType
    )

    $candidates = @()

    if ([System.IO.Path]::IsPathRooted($Path)) {
        $candidates += [System.IO.Path]::GetFullPath($Path)
    }
    else {
        $candidates += [System.IO.Path]::GetFullPath((Join-Path $repoRoot $Path))
        $candidates += [System.IO.Path]::GetFullPath((Join-Path $publishDir $Path))
    }

    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if (Test-Path -LiteralPath $candidate -PathType $PathType) {
            return $candidate
        }
    }

    $checked = ($candidates | Select-Object -Unique) -join "', '"
    throw "Unable to resolve source path '$Path'. Checked: '$checked'"
}

function Get-InstructionsMarkdown {
    param(
        [Parameter(Mandatory = $true)]
        [string]$InstructionsPath
    )

    # Parse the markdown source embedded in instructions.html so repository README mirrors
    # are guaranteed to track the same canonical content on every packaging run.
    $instructionsHtml = Get-Content -LiteralPath $InstructionsPath -Raw
    $pattern = '<script id="md-source" type="text/plain">(?s)(.*?)</script>'
    $match = [System.Text.RegularExpressions.Regex]::Match($instructionsHtml, $pattern)

    if (-not $match.Success) {
        throw "Unable to locate markdown source block in instructions file: $InstructionsPath"
    }

    $instructionsMarkdown = $match.Groups[1].Value.Trim()
    if ([string]::IsNullOrWhiteSpace($instructionsMarkdown)) {
        throw "Markdown source block in instructions file is empty: $InstructionsPath"
    }

    return $instructionsMarkdown
}

function Write-MarkdownFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$DestinationPath,

        [Parameter(Mandatory = $true)]
        [string]$Markdown
    )

    $readmeDirectory = Split-Path -Parent $DestinationPath
    New-Item -ItemType Directory -Path $readmeDirectory -Force | Out-Null
    Set-Content -LiteralPath $DestinationPath -Value $Markdown -Encoding utf8
}

function Test-FileContentEquals {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedContent
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $false
    }

    $actual = Get-Content -LiteralPath $Path -Raw
    $normalizedActual = $actual.Replace("`r`n", "`n").TrimEnd("`n")
    $normalizedExpected = $ExpectedContent.Replace("`r`n", "`n").TrimEnd("`n")
    return $normalizedActual -eq $normalizedExpected
}

function Sync-MarkdownMirror {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Markdown,

        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    # Keep README mirrors self-healing during packaging: when docs/instructions.html changes,
    # both repository markdown mirrors are rewritten in-place instead of failing the release job.
    if (-not (Test-FileContentEquals -Path $Path -ExpectedContent $Markdown)) {
        Write-MarkdownFile -DestinationPath $Path -Markdown $Markdown
        Write-Host "$Label refreshed from docs/instructions.html"
        return $true
    }

    Write-Host "$Label already up to date"
    return $false
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

$repoInstructionsPath = Join-Path $repoRoot "docs/instructions.html"
if (-not (Test-Path -LiteralPath $repoInstructionsPath -PathType Leaf)) {
    throw "Canonical instructions file not found: $repoInstructionsPath"
}

$instructionsMarkdown = Get-InstructionsMarkdown -InstructionsPath $repoInstructionsPath

$repoDocsReadmePath = Join-Path $repoRoot "docs/README.md"
$repoRootReadmePath = Join-Path $repoRoot "README.md"
$repoDocsReadmeUpdated = Sync-MarkdownMirror -Path $repoDocsReadmePath -Markdown $instructionsMarkdown -Label "docs/README.md"
$repoRootReadmeUpdated = Sync-MarkdownMirror -Path $repoRootReadmePath -Markdown $instructionsMarkdown -Label "README.md"

if ($repoDocsReadmeUpdated -or $repoRootReadmeUpdated) {
    Write-Host "README mirrors were refreshed in the repository checkout; downstream workflow steps can commit these updates."
}

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
            $sourceFile = Resolve-MappingSourcePath -Path $mapping.from -PathType Leaf

            $destFile = [System.IO.Path]::GetFullPath((Join-Path $stagingDir $mapping.to))
            $destParent = Split-Path -Parent $destFile
            New-Item -ItemType Directory -Path $destParent -Force | Out-Null

            Copy-Item -LiteralPath $sourceFile -Destination $destFile -Force
            Write-Host "Mapping[$i] file copied: $($mapping.from) -> $($mapping.to)"
        }

        'directory' {
            $sourceDir = Resolve-MappingSourcePath -Path $mapping.from -PathType Container

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

$stagingInstructionsPath = Join-Path $stagingDir "docs/instructions.html"
if (-not (Test-Path -LiteralPath $stagingInstructionsPath -PathType Leaf)) {
    throw "Required docs/instructions.html mapping is missing from staged package."
}

# Intentionally do NOT include a staged README.md in packaged release zips.
# The zip already ships with instructions.html as the primary end-user document.
# Keeping only one canonical doc avoids redundant files and reduces package clutter.
$stagingReadmePath = Join-Path $stagingDir "README.md"
if (Test-Path -LiteralPath $stagingReadmePath -PathType Leaf) {
    Remove-Item -LiteralPath $stagingReadmePath -Force
    Write-Host "Removed staged README.md to keep release package documentation non-redundant."
}

$artifactPath = [System.IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $stagingDir) $AssetName))
if (Test-Path -LiteralPath $artifactPath) {
    Remove-Item -LiteralPath $artifactPath -Force
}

Write-Host "Creating zip artifact at $artifactPath"
Compress-Archive -Path (Join-Path $stagingDir '*') -DestinationPath $artifactPath -CompressionLevel Optimal

"artifact_path=$artifactPath" | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
"staging_dir=$stagingDir" | Out-File -FilePath $env:GITHUB_OUTPUT -Encoding utf8 -Append
