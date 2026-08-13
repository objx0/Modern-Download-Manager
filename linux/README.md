# Linux port

This folder contains the Linux desktop application and browser native-messaging
host. It shares `ModernDownloadManager.Core` with the Windows application.

## Build

From the repository root:

```bash
dotnet restore linux/ModernDownloadManager.App/ModernDownloadManager.App.csproj
dotnet restore linux/ModernDownloadManager.NativeHost/ModernDownloadManager.NativeHost.csproj
dotnet build linux/ModernDownloadManager.App/ModernDownloadManager.App.csproj -c Release
dotnet build linux/ModernDownloadManager.NativeHost/ModernDownloadManager.NativeHost.csproj -c Release
```

Publish a self-contained x64 build:

```bash
dotnet publish linux/ModernDownloadManager.App/ModernDownloadManager.App.csproj \
  -c Release -r linux-x64 --self-contained true
```

The browser host can be registered with:

```bash
./linux/native-host-setup/install-native-host.sh \
  "$PWD/linux/ModernDownloadManager.NativeHost/bin/Release/net8.0/linux-x64/publish/ModernDownloadManager.NativeHost"
```

The existing Chrome extension can be used unchanged.
