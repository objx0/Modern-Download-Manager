const NATIVE_HOST = "com.moderndownloadmanager.host";
const MENU_ID = "send-to-mdm";

// --- Setup ---

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.create({
    id: MENU_ID,
    title: "Download with Modern Download Manager",
    contexts: ["link", "video", "audio", "image"]
  });

  chrome.storage.local.get({ autoIntercept: true, minimumCaptureSizeMB: 0 }, (settings) => {
    chrome.storage.local.set({ autoIntercept: settings.autoIntercept });
    chrome.storage.local.set({ minimumCaptureSizeMB: settings.minimumCaptureSizeMB });
  });
});

// --- Manual: right-click a link/video/audio/image ---

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  if (info.menuItemId !== MENU_ID) return;

  const url = info.srcUrl || info.linkUrl;
  if (!url) return;

  await sendToApp({
    url,
    referrer: tab?.url,
    userAgent: navigator.userAgent,
    cookie: await cookieHeaderFor(url)
  });
});

// --- Automatic: intercept the browser's own download flow ---
// Cancels the browser's native download the moment it starts and hands the
// same URL/filename off to the app instead — this is what gives IDM/FDM-style
// "every download goes through the manager" behavior rather than requiring
// a manual right-click every time.

chrome.downloads.onCreated.addListener(async (downloadItem) => {
  const { autoIntercept, minimumCaptureSizeMB } = await chrome.storage.local.get({ autoIntercept: true, minimumCaptureSizeMB: 0 });
  if (!autoIntercept) return;

  // totalBytes is -1 when Chromium cannot determine it before the download.
  // Unknown sizes are still forwarded; the app re-checks any known size.
  if (downloadItem.totalBytes >= 0 && minimumCaptureSizeMB > 0 &&
      downloadItem.totalBytes < minimumCaptureSizeMB * 1000000) return;

  // Avoid loops: don't re-intercept a download we already handed off (the
  // native host response doesn't create a browser download entry, so in
  // practice onCreated only fires for downloads we haven't seen yet — this
  // check is a defensive no-op guard in case a future Chrome version differs).
  if (downloadItem.byExtensionId === chrome.runtime.id) return;

  try {
    await chrome.downloads.cancel(downloadItem.id);
  } catch {
    // Already finished/cancelled by the time we got here — fine, skip forwarding
    // an item that already fully downloaded through Chrome's own path.
    return;
  }
  // Best-effort cleanup of the (likely empty/partial) file Chrome started writing.
  try { await chrome.downloads.erase({ id: downloadItem.id }); } catch { /* ignore */ }

  await sendToApp({
    url: downloadItem.finalUrl || downloadItem.url,
    suggestedFileName: downloadItem.filename?.split(/[\\/]/).pop() || undefined,
    referrer: downloadItem.referrer,
    userAgent: navigator.userAgent,
    cookie: await cookieHeaderFor(downloadItem.finalUrl || downloadItem.url),
    totalBytes: downloadItem.totalBytes
  });
});

// --- Shared plumbing ---

async function cookieHeaderFor(url) {
  try {
    const cookies = await chrome.cookies.getAll({ url });
    return cookies.map((c) => `${c.name}=${c.value}`).join("; ") || undefined;
  } catch {
    return undefined;
  }
}

function sendToApp(message) {
  return new Promise((resolve) => {
    chrome.runtime.sendNativeMessage(NATIVE_HOST, message, (response) => {
      if (chrome.runtime.lastError) {
        console.warn("Modern Download Manager: native host error —", chrome.runtime.lastError.message);
        notifyFailure();
      } else {
        notifySuccess();
      }
      resolve(response);
    });
  });
}

function notifySuccess() {
  chrome.action.setBadgeText({ text: "✓" });
  chrome.action.setBadgeBackgroundColor({ color: "#2e7d32" });
  setTimeout(() => chrome.action.setBadgeText({ text: "" }), 1500);
}

function notifyFailure() {
  chrome.action.setBadgeText({ text: "!" });
  chrome.action.setBadgeBackgroundColor({ color: "#c62828" });
  setTimeout(() => chrome.action.setBadgeText({ text: "" }), 3000);
}
