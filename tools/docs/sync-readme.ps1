$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../'))
$instructionsPath = Join-Path $repoRoot 'docs/instructions.html'
$rootReadmePath = Join-Path $repoRoot 'README.md'
$docsReadmePath = Join-Path $repoRoot 'docs/README.md'

function Get-InstructionsMarkdown {
    param([string]$InstructionsPath)

    $html = Get-Content -LiteralPath $InstructionsPath -Raw
    $pattern = '<script id="md-source" type="text/plain">(?s)(.*?)</script>'
    $match = [System.Text.RegularExpressions.Regex]::Match($html, $pattern)

    if (-not $match.Success) {
        throw "Unable to locate markdown source block in: $InstructionsPath"
    }

    $md = $match.Groups[1].Value.Trim()
    if ([string]::IsNullOrWhiteSpace($md)) {
        throw "Markdown source block is empty in: $InstructionsPath"
    }

    return $md
}

function Test-FileContentEquals {
    param([string]$Path, [string]$ExpectedContent)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }

    $actual = Get-Content -LiteralPath $Path -Raw
    $normalizedActual = $actual.Replace("`r`n", "`n").TrimEnd("`n")
    $normalizedExpected = $ExpectedContent.Replace("`r`n", "`n").TrimEnd("`n")
    return $normalizedActual -eq $normalizedExpected
}

function Sync-MarkdownMirror {
    param([string]$Path, [string]$Markdown, [string]$Label)

    if (-not (Test-FileContentEquals -Path $Path -ExpectedContent $Markdown)) {
        New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force | Out-Null
        Set-Content -LiteralPath $Path -Value $Markdown -Encoding utf8
        Write-Host "$Label updated"
        return $true
    }

    Write-Host "$Label already up to date"
    return $false
}

$markdown = Get-InstructionsMarkdown -InstructionsPath $instructionsPath

$changed = $false
$changed = (Sync-MarkdownMirror -Path $rootReadmePath -Markdown $markdown -Label 'README.md') -or $changed
$changed = (Sync-MarkdownMirror -Path $docsReadmePath -Markdown $markdown -Label 'docs/README.md') -or $changed

if ($changed) {
    Write-Host 'README mirrors updated.'
} else {
    Write-Host 'README mirrors are already in sync.'
}
