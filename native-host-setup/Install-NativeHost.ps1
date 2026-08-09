#Requires -Version 5.1
<##
.SYNOPSIS
  Registers the Modern Download Manager native messaging host for Chrome and Edge.
#>
param(
    [string]$NativeHostExePath = ""
)

$ErrorActionPreference = "Stop"

$nativeHostRoot = Join-Path $PSScriptRoot "..\ModernDownloadManager.NativeHost\bin"
if ([string]::IsNullOrWhiteSpace($NativeHostExePath)) {
    $foundNativeHost = Get-ChildItem $nativeHostRoot -Recurse -Filter "ModernDownloadManager.NativeHost.exe" -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($foundNativeHost) { $NativeHostExePath = $foundNativeHost.FullName }
} else {
    $resolvedNativeHost = Resolve-Path $NativeHostExePath -ErrorAction SilentlyContinue
    if ($resolvedNativeHost) { $NativeHostExePath = $resolvedNativeHost.Path }
}

if (-not $NativeHostExePath -or -not (Test-Path $NativeHostExePath)) {
    Write-Error "Couldn't find ModernDownloadManager.NativeHost.exe. Build the NativeHost project first."
    exit 1
}

$appExePath = Join-Path (Split-Path $NativeHostExePath) "ModernDownloadManager.App.exe"
if (-not (Test-Path $appExePath)) {
    Write-Warning "ModernDownloadManager.App.exe was not found next to the native host at: $appExePath"
}

$hostName = "com.moderndownloadmanager.host"
$manifestDir = Join-Path $env:LOCALAPPDATA "ModernDownloadManager\native-host-manifest"
New-Item -ItemType Directory -Force -Path $manifestDir | Out-Null
$manifestPath = Join-Path $manifestDir "$hostName.json"

$templatePath = Join-Path $PSScriptRoot "$hostName.json.template"
$escapedExePath = $NativeHostExePath -replace "\\", "\\"
$manifestJson = (Get-Content $templatePath -Raw) -replace "REPLACED_BY_INSTALL_SCRIPT", $escapedExePath
Set-Content -Path $manifestPath -Value $manifestJson -Encoding UTF8

Write-Host "Wrote manifest: $manifestPath"

function Register-Browser($registryRoot, $browserLabel) {
    $keyPath = "$registryRoot\$hostName"
    New-Item -Path $keyPath -Force | Out-Null
    Set-ItemProperty -Path $keyPath -Name "(default)" -Value $manifestPath
    Write-Host "Registered for $browserLabel at $keyPath"
}

Register-Browser "HKCU:\Software\Google\Chrome\NativeMessagingHosts" "Chrome"
Register-Browser "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts" "Edge"

Write-Host ""
Write-Host "Native messaging registration complete."
Write-Host "Load the extension from the extension folder in chrome://extensions or edge://extensions."
