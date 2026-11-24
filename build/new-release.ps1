<#
    Updates the version shared by the application and the Inno Setup installer.
    After reviewing CHANGELOG.md, push the commit and its vX.Y.Z tag; GitHub Actions
    will build, test, and publish the installer automatically.

    Example:
      powershell -ExecutionPolicy Bypass -File build\new-release.ps1 -Version 1.2.0
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptDir '..')
$propsPath = Join-Path $repoRoot 'Directory.Build.props'
$issPath = Join-Path $repoRoot 'installer\FutureSpeed.iss'
$utf8 = [System.Text.UTF8Encoding]::new($false)

$props = [System.IO.File]::ReadAllText($propsPath)
$props = [regex]::Replace($props, '<Version>[^<]+</Version>', "<Version>$Version</Version>")
$props = [regex]::Replace($props, '<AssemblyVersion>[^<]+</AssemblyVersion>', "<AssemblyVersion>$Version.0</AssemblyVersion>")
$props = [regex]::Replace($props, '<FileVersion>[^<]+</FileVersion>', "<FileVersion>$Version.0</FileVersion>")
[System.IO.File]::WriteAllText($propsPath, $props, $utf8)

$iss = [System.IO.File]::ReadAllText($issPath)
$iss = [regex]::Replace($iss, '#define\s+AppVersion\s+"[^"]+"', "#define AppVersion     `"$Version`"")
[System.IO.File]::WriteAllText($issPath, $iss, $utf8)

Write-Output "Updated Future Speed to $Version."
Write-Output 'Add the release notes to CHANGELOG.md, then run:'
Write-Output '  git add Directory.Build.props installer/FutureSpeed.iss CHANGELOG.md'
Write-Output "  git commit -m `"Release Future Speed $Version`""
Write-Output "  git tag v$Version"
Write-Output "  git push origin main v$Version"
