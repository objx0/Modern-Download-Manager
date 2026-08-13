#Requires -Version 5.1
<#!
.SYNOPSIS
  Builds and stages a distributable pre-alpha release.

.EXAMPLE
  .\Build-Release.ps1
  .\Build-Release.ps1 -Version 0.1.0 -SkipBuild
#>
param(
    [string]$Version = "0.1.0",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot
$stageRoot = Join-Path $repoRoot "artifacts\ModernDownloadManager-prealpha-$Version"
$appProject = Join-Path $repoRoot "ModernDownloadManager.App\ModernDownloadManager.App.csproj"
$hostProject = Join-Path $repoRoot "ModernDownloadManager.NativeHost\ModernDownloadManager.NativeHost.csproj"
$nugetConfig = Join-Path $repoRoot ".nuget\NuGet\NuGet.Config"
$nugetPackages = Join-Path $repoRoot ".nuget\packages"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "dotnet.exe was not found. Install the .NET 8 SDK first."
}

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

if (-not $SkipBuild) {
    & $msbuild $appProject /restore /t:Build /p:Configuration=Release /p:Platform=x64 /m "/p:RestoreConfigFile=$nugetConfig" "/p:RestorePackagesPath=$nugetPackages"
    if ($LASTEXITCODE -ne 0) { throw "The App Release build failed." }

    & dotnet restore $hostProject --runtime win-x64 --configfile $nugetConfig --packages $nugetPackages
    if ($LASTEXITCODE -ne 0) { throw "The NativeHost restore failed." }
    & dotnet publish $hostProject -c Release -r win-x64 --self-contained true --no-restore "/p:RestorePackagesPath=$nugetPackages"
    if ($LASTEXITCODE -ne 0) { throw "The NativeHost Release build failed." }
}

$appExe = Get-ChildItem (Join-Path $repoRoot "ModernDownloadManager.App\bin") -Recurse -Filter "ModernDownloadManager.App.exe" -File |
    Where-Object { $_.FullName -match "\\Release\\" -and $_.FullName -match "\\win-x64\\" } |
    Select-Object -First 1
$hostExe = Get-ChildItem (Join-Path $repoRoot "ModernDownloadManager.NativeHost\bin") -Recurse -Filter "ModernDownloadManager.NativeHost.exe" -File |
    Where-Object { $_.FullName -match "\\Release\\" -and $_.FullName -match "\\win-x64\\" } |
    Select-Object -First 1

if (-not $appExe) { throw "Could not find the Release app output. Build the App first." }
if (-not $hostExe) { throw "Could not find the Release native host output. Build the NativeHost first." }

if (Test-Path -LiteralPath $stageRoot) {
    Remove-Item -LiteralPath $stageRoot -Recurse -Force
}

$appStage = Join-Path $stageRoot "app"
$extensionStage = Join-Path $stageRoot "extension"
$firefoxExtensionStage = Join-Path $stageRoot "extension-firefox"
$nativeSetupStage = Join-Path $stageRoot "native-host-setup"
New-Item -ItemType Directory -Force -Path $appStage, $extensionStage, $firefoxExtensionStage, $nativeSetupStage | Out-Null

Copy-Item (Join-Path $appExe.Directory.FullName "*") $appStage -Recurse -Force
Copy-Item (Join-Path $hostExe.Directory.FullName "*") $appStage -Recurse -Force
Copy-Item (Join-Path $repoRoot "extension\*") $extensionStage -Recurse -Force
Copy-Item (Join-Path $repoRoot "extension-firefox\*") $firefoxExtensionStage -Recurse -Force
Copy-Item (Join-Path $repoRoot "native-host-setup\*") $nativeSetupStage -Recurse -Force
Copy-Item (Join-Path $repoRoot "README.md") $stageRoot -Force

Write-Host "Release staged at: $stageRoot" -ForegroundColor Green
Write-Host "Build installer with: ISCC.exe installer\ModernDownloadManager.iss" -ForegroundColor Cyan
