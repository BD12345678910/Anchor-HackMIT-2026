param(
    [Parameter(Mandatory = $true)][ValidatePattern('^[a-p]{32}$')][string]$ExtensionId,
    [string]$BridgePath = ''
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($BridgePath)) {
    $sibling = Join-Path $PSScriptRoot 'Anchor.NativeBridge.exe'
    $BridgePath = if (Test-Path -LiteralPath $sibling -PathType Leaf) {
        $sibling
    } else {
        Join-Path $PSScriptRoot '..\artifacts\Anchor-win-x64\Anchor.NativeBridge.exe'
    }
}
$resolvedBridge = [IO.Path]::GetFullPath($BridgePath)
if (-not (Test-Path -LiteralPath $resolvedBridge -PathType Leaf)) {
    throw "Anchor native bridge was not found: $resolvedBridge"
}

$manifestDirectory = Join-Path $env:LOCALAPPDATA 'Anchor\NativeMessaging'
New-Item -ItemType Directory -Path $manifestDirectory -Force | Out-Null
$manifestPath = Join-Path $manifestDirectory 'com.anchor.desktop.json'
$manifest = [ordered]@{
    name = 'com.anchor.desktop'
    description = 'Anchor desktop focus adapter'
    path = $resolvedBridge
    type = 'stdio'
    allowed_origins = @("chrome-extension://$ExtensionId/")
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding utf8

foreach ($key in @(
    'HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.anchor.desktop',
    'HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.anchor.desktop'
)) {
    New-Item -Path $key -Force | Out-Null
    Set-Item -Path $key -Value $manifestPath
}

Write-Host "Registered com.anchor.desktop for Chrome and Edge: $manifestPath"
