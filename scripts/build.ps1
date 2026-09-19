param(
    [switch]$SkipRestore,
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
$python = Join-Path $projectRoot '.venv\Scripts\python.exe'
$publishDirectory = Join-Path $projectRoot 'release\Anchor-win-x64'
$bridgeStaging = Join-Path $projectRoot 'artifacts\native-bridge-publish'

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

    & $python -m pytest 'src\Anchor.Worker\tests' -q -p no:cacheprovider --basetemp 'artifacts\pytest-release'
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
        $desktopExecutable = Join-Path $publishDirectory 'Anchor.exe'
        if (-not (Test-Path -LiteralPath $desktopExecutable -PathType Leaf)) { throw 'Desktop publish did not produce Anchor.exe.' }

        if (Test-Path -LiteralPath $bridgeStaging) {
            Remove-Item -LiteralPath $bridgeStaging -Recurse -Force
        }
        & $dotnet publish 'src\Anchor.NativeBridge\Anchor.NativeBridge.csproj' -c Release -r win-x64 --self-contained true --no-restore -o $bridgeStaging --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Native browser bridge publish failed.' }
        Copy-Item -LiteralPath (Join-Path $bridgeStaging 'Anchor.NativeBridge.exe') -Destination (Join-Path $publishDirectory 'Anchor.NativeBridge.exe') -Force

        & (Join-Path $PSScriptRoot 'build-worker.ps1') -OutputDirectory $publishDirectory
        if ($LASTEXITCODE -ne 0) { throw 'Vision worker publish failed.' }

        Copy-Item -LiteralPath 'browser\anchor-extension' -Destination (Join-Path $publishDirectory 'browser-extension') -Recurse -Force
        Copy-Item -LiteralPath 'scripts\register-browser-bridge.ps1' -Destination $publishDirectory -Force
        Copy-Item -LiteralPath 'scripts\unregister-browser-bridge.ps1' -Destination $publishDirectory -Force
        Copy-Item -LiteralPath 'README.md' -Destination $publishDirectory -Force
        Copy-Item -LiteralPath 'docs\privacy.md' -Destination (Join-Path $publishDirectory 'PRIVACY.md') -Force
        Copy-Item -LiteralPath 'docs\demo-script.md' -Destination (Join-Path $publishDirectory 'DEMO.md') -Force

        & (Join-Path $PSScriptRoot 'verify-release.ps1') -ReleaseDirectory $publishDirectory
        if ($LASTEXITCODE -ne 0) { throw 'Release verification failed.' }
        Write-Host "Published and verified Anchor release: $publishDirectory"
    }
}
finally {
    Pop-Location
}
