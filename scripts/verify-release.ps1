param(
    [string]$ReleaseDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'release\Anchor-win-x64')
)

$ErrorActionPreference = 'Stop'
$release = [IO.Path]::GetFullPath($ReleaseDirectory)
$required = @(
    'Anchor.exe',
    'Anchor.VisionWorker.exe',
    'camera-check.ps1',
    'README.md',
    'PRIVACY.md',
    'DEMO.md'
)
foreach ($relative in $required) {
    $path = Join-Path $release $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Release is incomplete; missing $relative"
    }
}

$healthText = & (Join-Path $release 'Anchor.VisionWorker.exe') --health-json
if ($LASTEXITCODE -ne 0) { throw 'Packaged vision worker health command failed.' }
$health = $healthText | ConvertFrom-Json
if ($health.protocolVersion -ne 3 -or $health.status -ne 'ok') {
    throw "Packaged worker protocol/health mismatch: $healthText"
}
if (-not $health.capabilities.camera.available) {
    throw "Packaged worker is missing required vision dependencies: $healthText"
}

$previousVerify = $env:ANCHOR_VERIFY_RELEASE
$env:ANCHOR_VERIFY_RELEASE = '1'
try {
    $endpoint = Join-Path $env:LOCALAPPDATA 'Anchor\bridge.json'
    if (Test-Path -LiteralPath $endpoint -PathType Leaf) {
        Remove-Item -LiteralPath $endpoint -Force
    }
    $launchTime = [DateTime]::UtcNow
    $process = Start-Process -FilePath (Join-Path $release 'Anchor.exe') -WorkingDirectory $release -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    $ready = $false
    while ([DateTime]::UtcNow -lt $deadline -and -not $process.HasExited) {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
        if ($process.Responding -and
            $process.MainWindowHandle -ne 0 -and
            (Test-Path -LiteralPath $endpoint -PathType Leaf) -and
            (Get-Item -LiteralPath $endpoint).LastWriteTimeUtc -ge $launchTime) {
            $ready = $true
            break
        }
    }
    if (-not $ready) { throw 'Anchor Settings did not become responsive with a live bridge endpoint.' }
    if (-not $process.WaitForExit(15000)) {
        Stop-Process -Id $process.Id -Force
        throw 'Anchor did not complete the graceful verification shutdown.'
    }
    if ($process.ExitCode -ne 0) { throw "Anchor exited with code $($process.ExitCode)." }
}
finally {
    $env:ANCHOR_VERIFY_RELEASE = $previousVerify
}

Write-Host "Verified release: $release"
