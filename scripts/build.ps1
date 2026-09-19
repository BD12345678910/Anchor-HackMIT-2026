param(
    [switch]$SkipRestore,
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$python = Join-Path $projectRoot '.venv\Scripts\python.exe'
$publishDirectory = Join-Path $projectRoot 'artifacts\Anchor-win-x64'

if (-not (Test-Path -LiteralPath $dotnet)) { throw 'Project-local .NET SDK not found under .tools\dotnet.' }
if (-not (Test-Path -LiteralPath $python)) { throw 'Python environment not found under .venv.' }

$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools\dotnet-home'
$env:NUGET_PACKAGES = Join-Path $env:DOTNET_CLI_HOME '.nuget\packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:PYTHONDONTWRITEBYTECODE = '1'

Push-Location $projectRoot
try {
    if (-not $SkipRestore) {
        & $dotnet restore 'Anchor.slnx' --ignore-failed-sources -p:NuGetAudit=false --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Dependency restore failed.' }
    }

    & $dotnet test 'Anchor.slnx' -c Release --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw '.NET tests failed.' }

    & $python -m pytest 'src\Anchor.Worker\tests' -q -p no:cacheprovider
    if ($LASTEXITCODE -ne 0) { throw 'Python tests failed.' }

    & node --test 'browser\anchor-extension\tests\*.test.mjs'
    if ($LASTEXITCODE -ne 0) { throw 'Browser tests failed.' }

    & $dotnet run --project 'src\Anchor.Demo\Anchor.Demo.csproj' -c Release --no-restore -- all
    if ($LASTEXITCODE -ne 0) { throw 'Replay audit failed.' }

    if (-not $SkipPublish) {
        $resolvedRoot = [IO.Path]::GetFullPath($projectRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        $resolvedPublish = [IO.Path]::GetFullPath($publishDirectory)
        if (-not $resolvedPublish.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clear a publish directory outside the project: $resolvedPublish"
        }
        if (Test-Path -LiteralPath $resolvedPublish) {
            Remove-Item -LiteralPath $resolvedPublish -Recurse -Force
        }
        & $dotnet publish 'src\Anchor.Desktop\Anchor.Desktop.csproj' -c Release -r win-x64 --self-contained true --no-restore -o $publishDirectory --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }
        Write-Host "Published Anchor to: $publishDirectory"
    }
}
finally {
    Pop-Location
}
