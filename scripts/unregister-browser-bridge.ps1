$ErrorActionPreference = 'Stop'
foreach ($key in @(
    'HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.anchor.desktop',
    'HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.anchor.desktop'
)) {
    if (Test-Path -LiteralPath $key) {
        Remove-Item -LiteralPath $key -Recurse -Force
    }
}

$manifestPath = Join-Path $env:LOCALAPPDATA 'Anchor\NativeMessaging\com.anchor.desktop.json'
if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
    Remove-Item -LiteralPath $manifestPath -Force
}
Write-Host 'Unregistered com.anchor.desktop from Chrome and Edge.'
