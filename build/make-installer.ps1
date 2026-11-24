<#
    Publishes the app and then compiles the Inno Setup installer.

    Inno Setup 6 must be installed (free / open source: https://jrsoftware.org/isdl.php).
    If ISCC.exe is not found, the script tells you exactly what to run.

    Example:
      powershell -ExecutionPolicy Bypass -File build\make-installer.ps1
#>

[CmdletBinding()]
param(
    [bool]$SelfContained = $true,
    [string]$IsccPath
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptDir '..')
$script = Join-Path $repoRoot 'installer\FutureSpeed.iss'

& (Join-Path $scriptDir 'publish.ps1') -SelfContained $SelfContained

if ([string]::IsNullOrWhiteSpace($IsccPath)) {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )
    $IsccPath = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
}

if ([string]::IsNullOrWhiteSpace($IsccPath) -or -not (Test-Path $IsccPath)) {
    Write-Warning @"
Inno Setup (ISCC.exe) was not found, so only the published folder was produced.
Install it with:   winget install JRSoftware.InnoSetup
Then run:          powershell -ExecutionPolicy Bypass -File build\make-installer.ps1
Or compile manually: "<Inno Setup folder>\ISCC.exe" "$script"
"@
    exit 2
}

Write-Output ''
Write-Output "Compiling installer with: $IsccPath"
& $IsccPath $script
if ($LASTEXITCODE -ne 0) { throw "ISCC failed with exit code $LASTEXITCODE" }

$output = Join-Path $repoRoot 'artifacts\installer'
Get-ChildItem $output -Filter *.exe | ForEach-Object {
    Write-Output ("Installer: {0} ({1} MB)" -f $_.FullName, [math]::Round($_.Length / 1MB, 1))
}
