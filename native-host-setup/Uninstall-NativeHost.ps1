#Requires -Version 5.1
$ErrorActionPreference = "SilentlyContinue"

$hostName = "com.moderndownloadmanager.host"
Remove-Item -LiteralPath "HKCU:\Software\Google\Chrome\NativeMessagingHosts\$hostName" -Recurse -Force
Remove-Item -LiteralPath "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\$hostName" -Recurse -Force
Remove-Item -LiteralPath (Join-Path $env:LOCALAPPDATA "ModernDownloadManager\native-host-manifest") -Recurse -Force
