param(
    [ValidateSet('all', 'focused-to-distracted', 'interrupted-and-returned', 'stuck-reading')]
    [string]$Scenario = 'all'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) {
    throw 'The project-local .NET SDK is missing. Run scripts\build.ps1 first.'
}

$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $env:DOTNET_CLI_HOME '.nuget\packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

Push-Location $projectRoot
try {
    & $dotnet run --project 'src\Anchor.Demo\Anchor.Demo.csproj' --no-restore -- $Scenario
    if ($LASTEXITCODE -ne 0) { throw "Replay failed with exit code $LASTEXITCODE." }
}
finally {
    Pop-Location
}
