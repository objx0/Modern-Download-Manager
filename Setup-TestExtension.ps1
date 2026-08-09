#Requires -Version 5.1
<#
.SYNOPSIS
  Builds and launches a disposable browser profile with the MDM extension loaded.

.EXAMPLE
  .\Setup-TestExtension.ps1
  .\Setup-TestExtension.ps1 -Browser Edge -SkipBuild
#>
param(
    [ValidateSet("Chrome", "Edge")]
    [string]$Browser = "Chrome",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot
$appProject = Join-Path $repoRoot "ModernDownloadManager.App\ModernDownloadManager.App.csproj"
$hostProject = Join-Path $repoRoot "ModernDownloadManager.NativeHost\ModernDownloadManager.NativeHost.csproj"
$extensionDir = Join-Path $repoRoot "extension"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "dotnet.exe was not found. Install the .NET 8 SDK first."
}

# WinUI 3's PRI packaging task is installed with Visual Studio. Using the
# standalone .NET 10 MSBuild can fail to locate that task, so prefer the VS
# MSBuild that owns the Windows App SDK tooling.
$msbuildCandidates = @(
    "${env:ProgramFiles}\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\amd64\MSBuild.exe",
    "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\amd64\MSBuild.exe",
    "${env:ProgramFiles}\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\amd64\MSBuild.exe",
    "${env:ProgramFiles}\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\amd64\MSBuild.exe"
)
$msbuild = $msbuildCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $msbuild) {
    throw "Visual Studio MSBuild was not found. Install the Visual Studio WinUI/Desktop development workload."
}

$runningApp = Get-Process -Name "ModernDownloadManager.App" -ErrorAction SilentlyContinue
if ($runningApp) {
    throw "ModernDownloadManager.App is running. Close it before building or deploying the test version, then run this script again."
}

if (-not $SkipBuild) {
    Write-Host "Building ModernDownloadManager.App ($Configuration, x64)..." -ForegroundColor Cyan
    & $msbuild $appProject /restore /t:Build /p:Configuration=$Configuration /p:Platform=x64 /m
    if ($LASTEXITCODE -ne 0) { throw "The App build failed." }

    Write-Host "Building ModernDownloadManager.NativeHost ($Configuration)..." -ForegroundColor Cyan
    & $msbuild $hostProject /restore /t:Build /p:Configuration=$Configuration /p:Platform=x64 /m
    if ($LASTEXITCODE -ne 0) { throw "The NativeHost build failed." }
}

$appExeCandidates = Get-ChildItem (Join-Path $repoRoot "ModernDownloadManager.App\bin") -Recurse -Filter "ModernDownloadManager.App.exe" -File | Where-Object { $_.FullName -match "\\$Configuration\\" }
$hostExeCandidates = Get-ChildItem (Join-Path $repoRoot "ModernDownloadManager.NativeHost\bin") -Recurse -Filter "ModernDownloadManager.NativeHost.exe" -File | Where-Object { $_.FullName -match "\\$Configuration\\" }
# Prefer the explicit win-x64 output. A previous framework-dependent build may
# still be present in the parent net8.0-windows folder.
$appExe = $appExeCandidates | Where-Object { $_.FullName -match "\\win-x64\\" } | Select-Object -First 1
$hostExe = $hostExeCandidates | Where-Object { $_.FullName -match "\\win-x64\\" } | Select-Object -First 1
if (-not $appExe) { $appExe = $appExeCandidates | Select-Object -First 1 }
if (-not $hostExe) { $hostExe = $hostExeCandidates | Select-Object -First 1 }
if (-not $appExe) { throw "Could not find ModernDownloadManager.App.exe. Build the App first." }
if (-not $hostExe) { throw "Could not find ModernDownloadManager.NativeHost.exe. Build the NativeHost first." }

# The native host launches the app from its own directory, so deploy the full
# App output beside it. This is intentionally a test deployment, not an install.
$appOutput = $appExe.Directory.FullName
$hostOutput = $hostExe.Directory.FullName
Write-Host "Deploying App beside NativeHost..." -ForegroundColor Cyan
Copy-Item (Join-Path $appOutput "*") $hostOutput -Recurse -Force

Write-Host "Starting Modern Download Manager..." -ForegroundColor Green
$deployedAppExe = Join-Path $hostOutput "ModernDownloadManager.App.exe"
Start-Process -FilePath $deployedAppExe -WorkingDirectory $hostOutput
Start-Sleep -Milliseconds 1200

$installer = Join-Path $repoRoot "native-host-setup\Install-NativeHost.ps1"
Write-Host "Registering native messaging for Chrome and Edge..." -ForegroundColor Cyan
& $installer -NativeHostExePath (Join-Path $hostOutput "ModernDownloadManager.NativeHost.exe")
if ($LASTEXITCODE -ne 0) { throw "Native host registration failed." }

$browserExe = if ($Browser -eq "Chrome") {
    @(
        (Join-Path $env:ProgramFiles "Google\Chrome\Application\chrome.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Google\Chrome\Application\chrome.exe"),
        (Join-Path $env:LOCALAPPDATA "Google\Chrome\Application\chrome.exe")
    )
} else {
    @(
        (Join-Path $env:ProgramFiles "Microsoft\Edge\Application\msedge.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Microsoft\Edge\Application\msedge.exe"),
        (Join-Path $env:LOCALAPPDATA "Microsoft\Edge\Application\msedge.exe")
    )
}
$browserPath = $browserExe | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $browserPath) { throw "$Browser was not found. Install it or run with -Browser Chrome/Edge." }

# A separate profile makes this safe to repeat and prevents interference with
# the user's normal browser profile. Close the test browser before rerunning.
$profileDir = Join-Path $env:LOCALAPPDATA "ModernDownloadManager\browser-test-profile-$($Browser.ToLowerInvariant())"
New-Item -ItemType Directory -Force -Path $profileDir | Out-Null
$browserArgs = @(
    "--user-data-dir=$profileDir",
    "--load-extension=$extensionDir",
    "--no-first-run",
    "--no-default-browser-check"
)

Write-Host "Launching $Browser test profile with the extension loaded..." -ForegroundColor Green
Start-Process -FilePath $browserPath -ArgumentList $browserArgs
Write-Host "" 
Write-Host "Test profile: $profileDir" -ForegroundColor DarkGray
Write-Host "Use the download URL above, or right-click a downloadable link and choose 'Download with Modern Download Manager'." -ForegroundColor Green
Write-Host "Close this test browser before running the script again." -ForegroundColor Yellow
