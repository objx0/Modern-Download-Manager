# Modern Download Manager 🚀

A lightweight Windows download manager focused on faster, resumable, multi-connection downloads.

> ⚠️ **Pre-alpha:** This project is still experimental. Expect bugs and breaking changes.

## Features ✨

- ⚡ Multi-connection downloads
- 🔁 Pause, resume, and continue downloads
- 📁 Download history and persistence
- 🚦 Queue and concurrent-download limits
- 🌐 Chrome and Edge browser integration
- 🖥️ Native Windows desktop app

## Download 📦

Download the latest installer from the GitHub **Releases** page:

    ModernDownloadManager-setup-0.1.0-prealpha.exe

The installer includes:

- Modern Download Manager
- Browser native host
- Chrome/Edge integration files
- Start Menu shortcuts
- Uninstaller

## Browser Extension 🌐

After installing the app:

1. Open chrome://extensions or edge://extensions.
2. Enable **Developer mode**.
3. Select **Load unpacked**.
4. Choose the installed extension folder.

The extension enables:

- Automatic download capture
- Right-click **Download with Modern Download Manager**
- Passing filenames, cookies, and referrers to the desktop app

The extension is currently distributed separately as a ZIP until it is published in the browser extension stores.

## Build from source 🛠️

Requirements:

- Windows 10 or newer
- Visual Studio with the WinUI/Desktop development workload
- .NET SDK
- Inno Setup 6 for building the installer

Build and stage a release:

    powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build-Release.ps1 -Version 0.1.0

Build the installer:

    & "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" .\installer\ModernDownloadManager.iss

The installer is created in:

    artifacts\installer\

## Current limitations ⚠️

- Windows only
- Unpackaged desktop application
- Browser extension requires manual loading
- Extension icons are placeholders
- No automatic update system yet

## License 📄

License information will be added before the first stable release.
