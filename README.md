# Modern Download Manager

A cross-platform, open-source download manager with resumable multi-connection transfers, queue control, persistent history, and browser capture.

> **Pre-alpha:** MDM is experimental software. Expect bugs, incomplete platform support, and breaking changes.

## Features

- Multi-connection downloads with automatic single-stream fallback
- Pause, resume, retry, cancellation, and persistent download history
- Queue and concurrent-download limits
- Per-category download folders and optional speed limits
- Browser capture from Chrome, Edge, and Firefox
- Native-messaging handoff with browser fallback if MDM is unavailable
- Capture prompt with filename, category, destination, and background-download options
- Progress reporting with downloaded size, speed, ETA, resume capability, and connection count
- Windows WinUI 3 desktop application
- Linux and macOS Avalonia ports sharing the same core engine

## Repository layout

| Path | Purpose |
| --- | --- |
| `ModernDownloadManager.Core/` | Shared downloader, queue, models, persistence, and local IPC |
| `ModernDownloadManager.App/` | Windows WinUI 3 application |
| `ModernDownloadManager.NativeHost/` | Windows browser native-messaging host |
| `extensions/chromium/` | Manifest V3 extension for Chrome and Edge |
| `extensions/firefox/` | Manifest V3 extension for Firefox |
| `extensions/tests/` | Node-based browser handoff tests |
| `linux/` | Linux Avalonia app, host, and registration scripts |
| `macos/` | macOS Avalonia app, host, and registration scripts |
| `native-host-setup/` | Windows native-host registration scripts |

## Releases

Release artifacts are published on the GitHub **Releases** page. A Windows release contains the desktop app, native host, browser extensions, native-host setup files, shortcuts, and uninstaller.

Linux and macOS are development builds; signed installers and packaged application bundles are not configured yet.

## Browser extensions

### Chrome and Edge

The Chromium extension is in `extensions/chromium/`. It uses Manifest V3 and supports automatic capture, right-click capture for links/media, browser-resolved filenames, active user-agent/referrer/cookie forwarding, a monitoring toggle, and a minimum capture-size setting.

Load it for development:

1. Build and register the native host as described below.
2. Open `chrome://extensions` or `edge://extensions`.
3. Enable **Developer mode**.
4. Choose **Load unpacked** and select `extensions/chromium/`.
5. Use the extension popup to turn monitoring on or off.

The manifest contains a fixed public extension key so the unpacked extension keeps the same ID. The private development key is not included in this repository.

### Firefox

The Firefox extension is in `extensions/firefox/` and supports the same capture, context-menu, popup, cookie, referrer, and user-agent features.

Load it temporarily:

1. Open `about:debugging#/runtime/this-firefox`.
2. Select **This Firefox**.
3. Choose **Load Temporary Add-on**.
4. Select `extensions/firefox/manifest.json`.

Temporary Firefox extensions are removed when Firefox closes. A signed package is required for permanent installation.

## Browser capture behavior

When monitoring is enabled, the extension pauses the browser download, waits for the browser-resolved filename, and sends the request to the native host. MDM then shows the capture prompt.

- **Start Download** accepts and starts the MDM transfer.
- **Download Later** queues it without starting immediately.
- **Download in background** closes the prompt after acceptance; the transfer continues in MDM and remains in the main list/tray.
- If capture is declined, unavailable, or disabled, the extension resumes the browser download.
- After MDM accepts an item, the extension cancels and removes the browser copy to avoid duplicates.

Cookies are held in memory for the active handoff and are not written to download history. Some sites use short-lived signed URLs, special headers, or media endpoints that cannot be downloaded outside the browser; those links may still fall back to the browser.

## Windows

### Requirements

- Windows 10 version 1809 or newer
- .NET 8 SDK
- Visual Studio with the WinUI/Desktop development workload
- Inno Setup 6 only when building the installer

### Build

From the repository root in PowerShell:

```powershell
dotnet restore ModernDownloadManager.App\ModernDownloadManager.App.csproj
dotnet build ModernDownloadManager.App\ModernDownloadManager.App.csproj -c Debug -p:Platform=x64
dotnet build ModernDownloadManager.NativeHost\ModernDownloadManager.NativeHost.csproj -c Debug -p:Platform=x64
```

The native host launches the app from its own output directory. Copy the complete Windows app output beside `ModernDownloadManager.NativeHost.exe`, or use the test setup script:

```powershell
.\Setup-TestExtension.ps1 -Browser Edge
```

Use `-Browser Chrome` or `-Browser Firefox` for the other supported browsers. Add `-SkipBuild` when matching outputs already exist.

### Register the native host manually

After building and copying the app beside the host:

```powershell
.\native-host-setup\Install-NativeHost.ps1 `
  -NativeHostExePath .\ModernDownloadManager.NativeHost\bin\x64\Debug\net8.0-windows\win-x64\ModernDownloadManager.NativeHost.exe
```

This registers the host for the current Windows user under Chrome, Edge, and Firefox. Run `native-host-setup\Uninstall-NativeHost.ps1` to remove those registrations.

### Release staging and installer

```powershell
.\Build-Release.ps1 -Version 0.1.0
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" .\installer\ModernDownloadManager.iss
```

Staged output is placed under `artifacts/`; the installer is created under the configured installer output directory.

## Linux

The Linux port uses Avalonia and shares `ModernDownloadManager.Core` with Windows and macOS.

### Requirements

- Linux desktop session
- .NET 8 SDK
- Chrome/Chromium or Firefox for browser capture

### Build, publish, and register

From the repository root:

```bash
dotnet restore linux/ModernDownloadManager.App/ModernDownloadManager.App.csproj
dotnet restore linux/ModernDownloadManager.NativeHost/ModernDownloadManager.NativeHost.csproj
dotnet build linux/ModernDownloadManager.App/ModernDownloadManager.App.csproj -c Release
dotnet build linux/ModernDownloadManager.NativeHost/ModernDownloadManager.NativeHost.csproj -c Release
dotnet publish linux/ModernDownloadManager.App/ModernDownloadManager.App.csproj -c Release -r linux-x64 --self-contained true
dotnet publish linux/ModernDownloadManager.NativeHost/ModernDownloadManager.NativeHost.csproj -c Release -r linux-x64 --self-contained true
chmod +x linux/ModernDownloadManager.NativeHost/bin/Release/net8.0/linux-x64/publish/ModernDownloadManager.NativeHost
./linux/native-host-setup/install-native-host.sh \
  "$PWD/linux/ModernDownloadManager.NativeHost/bin/Release/net8.0/linux-x64/publish/ModernDownloadManager.NativeHost"
```

Load `extensions/chromium/` in Chrome/Chromium or `extensions/firefox/manifest.json` temporarily in Firefox. The registration script writes manifests under `$XDG_CONFIG_HOME`, or `$HOME/.config` when that variable is unset.

## macOS

The macOS port uses Avalonia and shares `ModernDownloadManager.Core`. It is currently a development scaffold.

### Requirements

- macOS 12 or newer
- .NET 8 SDK
- Xcode Command Line Tools
- Chrome or Firefox for browser capture

### Build, publish, and register

From the repository root:

```bash
dotnet restore macos/ModernDownloadManager.App/ModernDownloadManager.App.csproj
dotnet restore macos/ModernDownloadManager.NativeHost/ModernDownloadManager.NativeHost.csproj
dotnet build macos/ModernDownloadManager.App/ModernDownloadManager.App.csproj -c Release
dotnet build macos/ModernDownloadManager.NativeHost/ModernDownloadManager.NativeHost.csproj -c Release
dotnet publish macos/ModernDownloadManager.App/ModernDownloadManager.App.csproj -c Release -r osx-arm64 --self-contained true
dotnet publish macos/ModernDownloadManager.NativeHost/ModernDownloadManager.NativeHost.csproj -c Release -r osx-arm64 --self-contained true
chmod +x macos/ModernDownloadManager.NativeHost/bin/Release/net8.0/osx-arm64/publish/ModernDownloadManager.NativeHost
./macos/native-host-setup/install-native-host.sh \
  "$PWD/macos/ModernDownloadManager.NativeHost/bin/Release/net8.0/osx-arm64/publish/ModernDownloadManager.NativeHost"
```

Use `osx-x64` instead of `osx-arm64` for Intel Macs. Load the Chromium or Firefox extension using the instructions above. Code signing, notarization, `.app` bundling, and signed extension packages are not configured yet.

## Tests

Run the shared core regression harness:

```bash
dotnet restore ModernDownloadManager.Core.Tests/ModernDownloadManager.Core.Tests.csproj
dotnet run --project ModernDownloadManager.Core.Tests/ModernDownloadManager.Core.Tests.csproj --no-restore
```

Run the extension handoff tests:

```bash
node extensions/tests/handoff.cjs
```

The tests cover queued, declined, failed, disconnected, parked-download, and browser-fallback paths.

## Security and local files

- Do not commit `extensions/dev-keys/`, browser profiles, `downloads.db3`, build output, or local settings.
- Native-host manifests contain local executable paths and should be generated on each machine.
- The extension requests broad host access because it must observe downloads across sites.
- Cookies are forwarded only for the active handoff and are kept in memory.

## Project status and license

Windows is the primary desktop release. Linux and macOS are development ports. Automatic updates, signed extension releases, stable packaging, and licensing information are not yet available.
