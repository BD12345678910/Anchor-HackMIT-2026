param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$python = Join-Path $projectRoot '.venv\Scripts\python.exe'
$workerRoot = Join-Path $projectRoot 'src\Anchor.Worker'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$work = Join-Path $projectRoot 'artifacts\pyinstaller-work'
$dist = Join-Path $projectRoot 'artifacts\pyinstaller-dist'
if (-not (Test-Path -LiteralPath $python -PathType Leaf)) { throw 'Project Python environment is missing.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
New-Item -ItemType Directory -Path $work -Force | Out-Null
New-Item -ItemType Directory -Path $dist -Force | Out-Null

$worker = Join-Path $dist 'Anchor.VisionWorker.exe'
$sourceFiles = @(
    Get-ChildItem -LiteralPath (Join-Path $workerRoot 'anchor_worker') -Recurse -File -ErrorAction SilentlyContinue
    Get-Item -LiteralPath (Join-Path $workerRoot 'worker_entry.py')
    Get-Item -LiteralPath (Join-Path $workerRoot 'Anchor.VisionWorker.spec')
    Get-Item -LiteralPath (Join-Path $workerRoot 'pyproject.toml')
)
$newestSource = $sourceFiles |
    Where-Object { $_.Extension -in @('.py', '.spec', '.toml', '.task') } |
    Sort-Object LastWriteTimeUtc -Descending |
    Select-Object -First 1
$needsBuild = $Force -or -not (Test-Path -LiteralPath $worker -PathType Leaf) -or
    ($null -ne $newestSource -and (Get-Item -LiteralPath $worker).LastWriteTimeUtc -lt $newestSource.LastWriteTimeUtc)
if ($needsBuild) {
    Push-Location $workerRoot
    try {
        & $python -m PyInstaller --noconfirm --clean --workpath $work --distpath $dist 'Anchor.VisionWorker.spec'
        if ($LASTEXITCODE -ne 0) { throw 'Vision worker packaging failed.' }
    }
    finally {
        Pop-Location
    }
}
if (-not (Test-Path -LiteralPath $worker -PathType Leaf)) { throw 'Packaged vision worker was not produced.' }
Copy-Item -LiteralPath $worker -Destination (Join-Path $output 'Anchor.VisionWorker.exe') -Force
$healthText = & (Join-Path $output 'Anchor.VisionWorker.exe') --health-json
if ($LASTEXITCODE -ne 0) { throw 'Packaged worker health check failed.' }
$health = $healthText | ConvertFrom-Json
if ($health.status -ne 'ok' -or $health.protocolVersion -ne 3) {
    throw "Packaged worker protocol mismatch: $healthText"
}
Write-Host "Packaged vision worker: $(Join-Path $output 'Anchor.VisionWorker.exe')"
