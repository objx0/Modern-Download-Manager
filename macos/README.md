# macOS port

This folder contains the macOS desktop application and browser native-messaging
host. Both share `ModernDownloadManager.Core` with the Windows and Linux ports.

## Requirements

- macOS 12 or newer
- .NET 8 SDK
- Xcode Command Line Tools

## Build

From the repository root:

```bash
dotnet restore macos/ModernDownloadManager.App/ModernDownloadManager.App.csproj
dotnet restore macos/ModernDownloadManager.NativeHost/ModernDownloadManager.NativeHost.csproj
dotnet build macos/ModernDownloadManager.App/ModernDownloadManager.App.csproj -c Release
dotnet build macos/ModernDownloadManager.NativeHost/ModernDownloadManager.NativeHost.csproj -c Release
```

Publish a self-contained Apple Silicon build:

```bash
dotnet publish macos/ModernDownloadManager.App/ModernDownloadManager.App.csproj \
  -c Release -r osx-arm64 --self-contained true
dotnet publish macos/ModernDownloadManager.NativeHost/ModernDownloadManager.NativeHost.csproj \
  -c Release -r osx-arm64 --self-contained true
```

Use `osx-x64` for Intel Macs. The native host can be registered for Chrome and
Firefox with:

```bash
./macos/native-host-setup/install-native-host.sh \
  "$PWD/macos/ModernDownloadManager.NativeHost/bin/Release/net8.0/osx-arm64/publish/ModernDownloadManager.NativeHost"
```

The existing browser extensions can be used unchanged.

> Status: development scaffold. Code signing, notarization, and a distributable
> `.app` bundle are not configured yet.
