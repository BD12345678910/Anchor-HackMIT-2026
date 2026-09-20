<#
.SYNOPSIS
    Explains why Anchor can or cannot see your webcam. Run it from the folder that contains
    Anchor.exe (the release zip) or from a source checkout:

        powershell -ExecutionPolicy Bypass -File .\camera-check.ps1

    It prints three things and never sends anything anywhere:
      1. the cameras Windows knows about (Plug and Play + the same WinRT class Anchor uses),
      2. whether desktop apps are allowed to use the camera (Settings > Privacy & security > Camera),
      3. what Anchor's vision worker (OpenCV) sees for every camera index and backend, and whether
         frames actually arrive - a device can "open" yet deliver nothing while Teams/Zoom hold it.
#>
param(
    [string]$ReleaseDirectory = $PSScriptRoot
)

$ErrorActionPreference = 'Continue'

function Section($title) { Write-Host ""; Write-Host "== $title ==" -ForegroundColor Cyan }

Section 'Windows camera devices (Plug and Play)'
$pnp = Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue |
    Where-Object { $_.Class -in @('Camera', 'Image') -or $_.FriendlyName -match 'camera|webcam' }
if (-not $pnp) {
    Write-Host 'None. Windows itself does not see a camera: check the USB cable/hub, or the laptop camera kill switch (Fn key / slider).' -ForegroundColor Yellow
} else {
    $pnp | Format-Table FriendlyName, Status, Class, InstanceId -AutoSize | Out-String | Write-Host
    $broken = $pnp | Where-Object Status -ne 'OK'
    if ($broken) { Write-Host 'A device is not in status OK: open Device Manager and update or re-enable its driver.' -ForegroundColor Yellow }
}

Section 'WinRT video-capture enumeration (what Anchor.exe asks first)'
try {
    Add-Type -AssemblyName System.Runtime.WindowsRuntime -ErrorAction Stop
    $null = [Windows.Devices.Enumeration.DeviceInformation, Windows.Devices.Enumeration, ContentType = WindowsRuntime]
    $op = [Windows.Devices.Enumeration.DeviceInformation]::FindAllAsync([Windows.Devices.Enumeration.DeviceClass]::VideoCapture)
    $asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
        $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
    })[0].MakeGenericMethod([Windows.Devices.Enumeration.DeviceInformationCollection])
    $devices = $asTask.Invoke($null, @($op)).GetAwaiter().GetResult()
    if ($devices.Count -eq 0) { Write-Host 'WinRT reports 0 video-capture devices.' -ForegroundColor Yellow }
    foreach ($device in $devices) { Write-Host (" - {0}  [{1}]" -f $device.Name, $device.Id) }
} catch {
    Write-Host "WinRT enumeration not available from this PowerShell ($($_.Exception.Message)); Anchor.exe still performs it natively."
}

Section 'Camera privacy settings'
$global = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam' -ErrorAction SilentlyContinue
$user = Get-ItemProperty 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam' -ErrorAction SilentlyContinue
$desktop = Get-ItemProperty 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam\NonPackaged' -ErrorAction SilentlyContinue
Write-Host ("Camera access (all users):        {0}" -f ($(if ($global) { $global.Value } else { 'not set (Allow)' })))
Write-Host ("Let apps access your camera:      {0}" -f ($(if ($user) { $user.Value } else { 'not set (Allow)' })))
Write-Host ("Let desktop apps access camera:   {0}" -f ($(if ($desktop) { $desktop.Value } else { 'not set (Allow)' })))
if (($global -and $global.Value -eq 'Deny') -or ($user -and $user.Value -eq 'Deny') -or ($desktop -and $desktop.Value -eq 'Deny')) {
    Write-Host 'A camera privacy switch is set to Deny. Open Settings > Privacy & security > Camera and enable "Camera access", "Let apps access your camera" and "Let desktop apps access your camera".' -ForegroundColor Yellow
}

Section 'Apps currently holding the camera'
$holders = Get-ChildItem 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam\NonPackaged' -ErrorAction SilentlyContinue |
    ForEach-Object { Get-ItemProperty $_.PSPath } |
    Where-Object { $_.LastUsedTimeStop -eq 0 -and $_.LastUsedTimeStart -gt 0 }
if ($holders) {
    $holders | ForEach-Object { Write-Host (" - {0}" -f ($_.PSChildName -replace '#', '\')) -ForegroundColor Yellow }
    Write-Host 'Close these apps (Teams, Zoom, browser tabs with camera access) and run the check again.'
} else {
    Write-Host 'No desktop app is currently using the camera.'
}

Section 'Anchor vision worker probe (OpenCV: DirectShow, Media Foundation, auto)'
$worker = Join-Path $ReleaseDirectory 'Anchor.VisionWorker.exe'
$json = $null
if (Test-Path -LiteralPath $worker -PathType Leaf) {
    $json = & $worker --camera-diagnose-json 2>$null
} else {
    $repo = Split-Path -Parent $PSScriptRoot
    $python = Join-Path $repo '.venv\Scripts\python.exe'
    if (Test-Path -LiteralPath $python -PathType Leaf) {
        $json = & $python -m anchor_worker --camera-diagnose-json 2>$null
    } else {
        Write-Host "Neither $worker nor $python exists. Run this script from the extracted release folder." -ForegroundColor Yellow
    }
}
if ($json) {
    $report = $json | ConvertFrom-Json
    if ($report.status -ne 'ok') {
        Write-Host "Worker could not probe cameras: $($report.error)" -ForegroundColor Yellow
    } else {
        Write-Host "OpenCV $($report.opencv)"
        $working = @($report.probes | Where-Object { $_.frames })
        $report.probes | Where-Object { $_.opened -or $_.error -ne 'not opened' } |
            Format-Table index, backend, opened, frames, width, height, error -AutoSize | Out-String | Write-Host
        if ($working.Count -gt 0) {
            Write-Host ("Anchor can use camera index {0}. In Anchor: Camera > Refresh cameras > pick it > Test gaze." -f (($working | Select-Object -First 1).index + 1)) -ForegroundColor Green
        } else {
            Write-Host 'No camera delivered frames to OpenCV. If Windows lists one above, the usual causes are the privacy switch, another app holding the device, or a vendor "camera companion" app with exclusive mode.' -ForegroundColor Yellow
        }
    }
}
