<#
    Publishes Future Speed File Transfer for Windows x64.

    Examples:
      powershell -ExecutionPolicy Bypass -File build\publish.ps1
      powershell -ExecutionPolicy Bypass -File build\publish.ps1 -SelfContained:$false
#>

[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [string]$Runtime = 'win-x64',

    # Self-contained by default: the installed app then needs no .NET runtime on the machine.
    [bool]$SelfContained = $true,

    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Resolve-Path (Join-Path $scriptDir '..')
$project = Join-Path $repoRoot 'src\FutureSpeed.App\FutureSpeed.App.csproj'

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repoRoot "artifacts\publish\$Runtime"
}

Write-Output "Project       : $project"
Write-Output "Configuration : $Configuration"
Write-Output "Runtime       : $Runtime"
Write-Output "SelfContained : $SelfContained"
Write-Output "Output        : $OutputPath"
Write-Output ''

# The icon is generated rather than committed as a binary blob.
& (Join-Path $scriptDir 'make-icon.ps1')

if (Test-Path $OutputPath) { Remove-Item $OutputPath -Recurse -Force }

$arguments = @(
    'publish', $project,
    '-c', $Configuration,
    '-r', $Runtime,
    "--self-contained", $SelfContained.ToString().ToLowerInvariant(),
    '-o', $OutputPath,
    '/p:DebugType=None',
    '/p:DebugSymbols=false'
)

& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

$exe = Join-Path $OutputPath 'FutureSpeed.exe'
if (-not (Test-Path $exe)) { throw "FutureSpeed.exe was not produced in $OutputPath" }

$size = [math]::Round(((Get-ChildItem $OutputPath -Recurse | Measure-Object -Property Length -Sum).Sum / 1MB), 1)
Write-Output ''
Write-Output "Published: $exe"
Write-Output "Total size: $size MB"
