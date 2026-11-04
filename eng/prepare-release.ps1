<#
.SYNOPSIS
    Prepares the repository for a release: ships the public API and analyzer baselines, sets the version and dates the
    changelog. Run it on a release branch, review the diff, commit, then tag vX.Y.Z (see docs/releasing.md).

.DESCRIPTION
    - Moves every PublicAPI.Unshipped.txt entry into PublicAPI.Shipped.txt (sorted) and resets Unshipped.
    - Moves the new rules of src/DotNetQuery.SourceGenerators/AnalyzerReleases.Unshipped.md into a
      "## Release X.Y.Z" section of AnalyzerReleases.Shipped.md and resets Unshipped.
    - Sets VersionPrefix in Directory.Build.props.
    - Renames the changelog's "## Unreleased" heading to "## X.Y.Z (yyyy-MM-dd)".
    Use -DryRun to print what would change without writing anything.

.EXAMPLE
    pwsh eng/prepare-release.ps1 -Version 0.9.0
    pwsh eng/prepare-release.ps1 -Version 1.0.0 -DryRun
#>
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version,

    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Write-Text([string] $path, [string] $text) {
    if ($DryRun) { return }
    [System.IO.File]::WriteAllText($path, $text, $utf8)
}

function Read-Lines([string] $path) {
    return @([System.IO.File]::ReadAllLines($path) | Where-Object { $_ -ne $null })
}

# 1. Public API baselines.
$shippedTotal = 0
Get-ChildItem -Path (Join-Path $root 'src') -Recurse -Filter 'PublicAPI.Unshipped.txt' |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
    ForEach-Object {
        $unshippedPath = $_.FullName
        $shippedPath = Join-Path $_.DirectoryName 'PublicAPI.Shipped.txt'
        $unshipped = Read-Lines $unshippedPath | Where-Object { $_ -and -not $_.StartsWith('#') }
        if ($unshipped.Count -eq 0) { return }

        $shipped = if (Test-Path $shippedPath) { Read-Lines $shippedPath | Where-Object { $_ -and -not $_.StartsWith('#') } } else { @() }
        $merged = @($shipped + $unshipped | Sort-Object -Unique -CaseSensitive)
        $project = Split-Path -Leaf $_.DirectoryName
        Write-Host ("{0}: shipping {1} API entries ({2} total)" -f $project, $unshipped.Count, $merged.Count)
        $shippedTotal += $unshipped.Count
        Write-Text $shippedPath (((@("#nullable enable") + $merged) -join "`n") + "`n")
        Write-Text $unshippedPath "#nullable enable`n"
    }

# 2. Analyzer release tracking.
$analyzerDir = Join-Path $root 'src/DotNetQuery.SourceGenerators'
$analyzerUnshipped = Join-Path $analyzerDir 'AnalyzerReleases.Unshipped.md'
$analyzerShipped = Join-Path $analyzerDir 'AnalyzerReleases.Shipped.md'
$header = @('; Unshipped analyzer release', '; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md')
$unshippedLines = Read-Lines $analyzerUnshipped
$body = @($unshippedLines | Where-Object { -not $_.StartsWith(';') })
$hasRules = @($body | Where-Object { $_ -match '^\s*DNQ\d+' }).Count -gt 0
if ($hasRules) {
    $trimmed = ($body -join "`n").Trim()
    $shippedText = [System.IO.File]::ReadAllText($analyzerShipped).TrimEnd()
    $section = "## Release $Version`n`n$trimmed"
    Write-Host "Analyzers: shipping $(@($body | Where-Object { $_ -match '^\s*DNQ\d+' }).Count) rule(s) as release $Version"
    Write-Text $analyzerShipped ($shippedText + "`n`n" + $section + "`n")
    Write-Text $analyzerUnshipped (($header -join "`n") + "`n")
}

# 3. Version.
$props = Join-Path $root 'Directory.Build.props'
$propsText = [System.IO.File]::ReadAllText($props)
$newProps = [regex]::Replace($propsText, '<VersionPrefix>[^<]*</VersionPrefix>', "<VersionPrefix>$Version</VersionPrefix>")
Write-Host "Directory.Build.props: VersionPrefix -> $Version"
Write-Text $props $newProps

# 4. Changelog.
$changelog = Join-Path $root 'CHANGELOG.md'
$changelogText = [System.IO.File]::ReadAllText($changelog)
$date = (Get-Date).ToString('yyyy-MM-dd')
$match = [regex]::Match($changelogText, '(?m)^## Unreleased[^\r\n]*')
if ($match.Success) {
    Write-Host "CHANGELOG.md: '$($match.Value)' -> '## $Version ($date)'"
    Write-Text $changelog ($changelogText.Substring(0, $match.Index) + "## $Version ($date)" + $changelogText.Substring($match.Index + $match.Length))
}
else {
    Write-Warning "CHANGELOG.md has no '## Unreleased' section; add the release notes by hand."
}

Write-Host ("Done{0}: {1} API entries shipped. Review the diff, update docs/migration.md, commit, and tag v{2}." -f ($(if ($DryRun) { ' (dry run, nothing written)' } else { '' })), $shippedTotal, $Version)
