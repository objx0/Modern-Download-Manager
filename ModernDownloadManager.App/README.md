# Modern Download Manager — Phase 1 + 2: Core Engine + WinUI 3 App

This is the `ModernDownloadManager.Core` class library — the pure-logic layer with
no UI or platform dependencies, so it's testable and reusable from the WinUI 3 app
and the native-messaging host alike.

## What's here

- **Models/** — `DownloadItem`, `DownloadSegment`, `DownloadState`, `DownloadCategory`
  (with auto extension→category resolution, IDM/FDM-style).
- **Engine/**
  - `SegmentedDownloader` — the core multi-connection engine. Probes the server
    with HEAD (falling back to a ranged GET) to check `Accept-Ranges`, splits the
    file into up to 16 byte-range segments, downloads them in parallel, and merges
    them on completion. Falls back to a single stream automatically when the
    server doesn't support ranges or the size is unknown.
  - Resume works by reading how many bytes already exist in each segment's temp
    file and requesting only the remainder — survives app restarts and pauses.
  - `ThrottledStream` — per-download bandwidth cap (leaky-bucket), so speed
    limiting is just setting a property, no separate throttling subsystem.
- **Persistence/** — `DownloadRepository`, SQLite-backed (`sqlite-net-pcl`), for
  the queue + history to survive restarts.
- **Scheduling/** — `DownloadQueueManager`, the single object the UI/native host
  will call: `EnqueueAsync`, `PauseAsync`, `ResumeAsync`, `CancelAsync`, plus
  `MaxConcurrentDownloads` gating so 20 queued files don't all fight for bandwidth
  at once.

## Phase 2: `ModernDownloadManager.App` (WinUI 3)

- **Unpackaged** (no MSIX / `Package.appxmanifest`) for now — builds and runs
  straight from Visual Studio (F5) without needing icon assets or a packaging
  identity. Switch `WindowsPackageType` to `MSIX` later if you want Store
  distribution or an installer; that's a small, self-contained change.
- **`App.xaml.cs`** is the composition root: builds the `HttpClient` →
  `SegmentedDownloader` → `DownloadRepository` → `DownloadQueueManager` chain
  and hands the queue manager to the main view model. No DI container — just a
  few `new`s, since the object graph is small.
- **`MainWindow`** — `NavigationView` sidebar (All / Active / Completed +
  per-category filters) with a Mica backdrop (`MicaKind.BaseAlt`, matching the
  FDM app's look), falling back gracefully via `MicaController.IsSupported()`
  on older Windows builds.
- **`MainViewModel` / `DownloadItemViewModel`** — CommunityToolkit.Mvvm
  `ObservableObject`s. The item view model exposes `PauseCommand` /
  `ResumeCommand` / `RemoveCommand`, each just delegating to
  `DownloadQueueManager`; all real logic stays in Core.
- Progress/state events from the engine are marshalled onto the UI thread via
  `DispatcherQueue.TryEnqueue` — the engine itself is thread-agnostic.
- The add-URL box currently drops everything into the user's `Downloads`
  folder; a folder picker + per-download destination is an easy Phase 2.5 add.

## Phase 2.1: Native title bar + Settings page

- **Title bar** — replaced the default (non-Fluent-looking) system title bar with
  WinUI 3's native `TitleBar` XAML control (`Microsoft.UI.Xaml.Controls.TitleBar`,
  shipped in Windows App SDK 1.6+). It's Mica-aware and integrates with
  `NavigationView` — the pane-toggle button now lives in the title bar itself
  rather than duplicated inside the nav pane. Wiring: `ExtendsContentIntoTitleBar
  = true` + `SetTitleBar(AppTitleBar)` in `MainWindow.xaml.cs`, with the system
  caption buttons (min/max/close) set transparent so Mica shows through them.
- **Settings page** — `NavigationView`'s built-in Settings item now actually does
  something: `MainViewModel.IsSettingsView` toggles between the downloads panel
  and a settings panel (two `Grid`s with converter-driven visibility, since this
  app isn't using `Frame`-based page navigation). Settings covers: default
  download folder (with a native folder picker), max simultaneous downloads,
  default speed limit (MB/s), default segment count.
- **Persistence** — `AppSettings` (Core) + `AppSettingsStore` (Core,
  JSON-file-backed at `%LocalAppData%\ModernDownloadManager\settings.json`).
  Deliberately *not* using `Windows.Storage.ApplicationData`, since that API
  assumes package identity and is flaky for unpackaged apps.
- **One caveat**: `MaxConcurrentDownloads` is read once at app startup to size
  the queue's `SemaphoreSlim`, which can't safely shrink its capacity mid-run.
  Changing it on the Settings page saves immediately but takes effect on next
  launch — the UI says so next to the Save button.

## Phase 3: Browser capture — NativeHost + extension (Chrome/Edge)

This is what makes downloads actually flow from the browser into the app,
either automatically or via right-click.

### `ModernDownloadManager.NativeHost`
A tiny, short-lived console app (`WinExe` subsystem so it doesn't flash a
console window) that Chrome/Edge spawn fresh for every
`chrome.runtime.sendNativeMessage` call. It speaks Chrome's native-messaging
wire format (4-byte little-endian length prefix + UTF-8 JSON on stdin/stdout),
then:
1. Tries handing the request to an already-running app instance over a local
   named pipe (`ModernDownloadManager.Core.Ipc.DownloadPipe`, shared with the
   App project).
2. If nothing's listening, launches `ModernDownloadManager.App.exe` (expected
   next to the host — see install script) with `--add-download=<base64 JSON>`,
   which `App.xaml.cs` picks up on cold start.

### `extension/` (Manifest V3, Chrome + Edge — one codebase)
- **Automatic capture** (default on): `chrome.downloads.onCreated` cancels the
  browser's own download the instant it starts and hands the URL + browser's
  own resolved filename + referrer + cookies to the native host instead. This
  is what gives the IDM/FDM-style "every download goes through the manager"
  behavior — and since the browser already resolved the filename properly,
  it sidesteps all the guessing the engine has to do for a pasted-in URL.
- **Manual capture**: right-click a link/video/audio/image → "Download with
  Modern Download Manager" — useful for media that isn't a browser-native
  "download" (e.g. a `<video>` element's source).
- **Toggle**: the toolbar popup has an on/off switch for automatic capture, in
  case you sometimes want the browser's own download bar instead.
- **Fixed extension ID**: `manifest.json` embeds a `key` (from a keypair I
  generated for this project — the private half is in `extension-dev-keys/`,
  gitignore it) so the extension ID stays the same
  (`gpflfhfbgjbijbocjdjngdfkohaojmaf`) across reloads. This matters because the
  native host's `allowed_origins` has to name the exact extension ID, and that
  ID would otherwise change every time you reload an unpacked extension.
- Icons in `extension/icons/` are placeholder art — swap them for real branding
  whenever you're ready; they're not load-bearing for functionality.

### One-time setup on your Windows machine

1. Build both `ModernDownloadManager.App` and `ModernDownloadManager.NativeHost`
   (same Configuration, e.g. both Debug).
2. Copy `ModernDownloadManager.App.exe` (and its output folder contents) into
   the *same folder* as `ModernDownloadManager.NativeHost.exe` — the host
   launches the app by relative path when nothing's running. (For now, simplest
   is just building NativeHost with a post-build step or manually copying; I
   can wire up an MSBuild target to do this automatically if it gets annoying.)
3. Run `native-host-setup\Install-NativeHost.ps1` in PowerShell. It writes the
   native-messaging manifest and registers it for both Chrome and Edge under
   `HKCU` (no admin needed). Re-run it if the NativeHost.exe path ever changes.
4. In Chrome or Edge: go to `chrome://extensions` (or `edge://extensions`),
   enable **Developer mode**, click **Load unpacked**, and select the
   `extension/` folder.
5. Confirm the loaded extension's ID matches `gpflfhfbgjbijbocjdjngdfkohaojmaf`
   (shown on the extensions page) — it should, since the key is fixed in
   `manifest.json`.
6. Test: with the app closed, trigger any browser download. It should get
   cancelled in the browser and the app should launch with the download
   already queued. Then with the app open, try another — it should just
   appear in the list without a new process launching.

### Known rough edges (fine for now, worth knowing)
- If the extension's native-messaging call fails (host not registered, or a
  typo in the manifest path), Chrome just returns `chrome.runtime.lastError`
  silently — the toolbar badge turns red (`!`) for 3 seconds as the only
  signal. Worth adding a proper notification/toast later.
- Cookie forwarding sends *all* cookies for the URL's origin as a flat header
  during the active request, but cookies are no longer persisted in SQLite.
  Same-site/partitioned cookie edge cases still depend on browser behavior.
- No uninstall script yet — removing the registry keys and unloading the
  extension is manual for now.

## Not yet built (next phases)

1. System tray icon + jump list (minimize-to-tray, quick pause-all).
2. An MSBuild step or publish profile that automatically copies App output
   next to NativeHost output, instead of the manual copy step in Phase 3 setup.
3. Real extension icon art (current icons are placeholder).
4. Uninstall script for the native-messaging registry keys.

## A note on building

I scaffolded this without running a NuGet restore/build — my sandbox can't reach
`nuget.org`, only a fixed allowlist (GitHub, PyPI, npm, crates.io). Pull this into
Visual Studio and it should restore `sqlite-net-pcl` and build against `net8.0`
cleanly; if anything doesn't compile on your machine, send me the error and I'll
fix it directly.

## Suggested next step

Say the word and I'll build the `NativeHost` + browser `extension` next — that's
what makes it feel like IDM/FDM (right-click "Download with Modern Download
Manager", automatic interception of big files) instead of a manual paste-a-URL
tool.
