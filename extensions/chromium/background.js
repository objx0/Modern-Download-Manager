const NATIVE_HOST = "com.moderndownloadmanager.host";
const MENU_ID = "send-to-mdm";
console.info("Modern Download Manager extension loaded", { version: chrome.runtime.getManifest().version });

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.create({ id: MENU_ID, title: "Download with Modern Download Manager", contexts: ["link", "video", "audio", "image"] });
  chrome.storage.local.get({ autoIntercept: true, minimumCaptureSizeMB: 0 }, (settings) => {
    chrome.storage.local.set(settings);
    updateMonitoringBadge(settings.autoIntercept);
  });
});

chrome.storage.onChanged.addListener((changes, areaName) => {
  if (areaName === "local" && changes.autoIntercept)
    updateMonitoringBadge(changes.autoIntercept.newValue === true);
});

chrome.storage.local.get({ autoIntercept: true }, ({ autoIntercept }) => updateMonitoringBadge(autoIntercept));

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  if (info.menuItemId !== MENU_ID) return;
  const url = info.srcUrl || info.linkUrl;
  if (!url) return;
  await sendToApp({ url, referrer: tab?.url, userAgent: navigator.userAgent, cookie: await cookieHeaderFor(url) });
});

// Chromium exposes the resolved filename a little later than onCreated. Keep
// the browser transfer parked while that decision is made, just like Hydra.
// This avoids capturing a generic name such as "download" when the server's
// Content-Disposition header contains the real filename.
const parkedDownloads = new Map();

async function parkDownload(downloadItem) {
  console.info("MDM download created", { id: downloadItem.id, url: downloadItem.url, filename: downloadItem.filename || "" });
  const { autoIntercept, minimumCaptureSizeMB } = await chrome.storage.local.get({ autoIntercept: true, minimumCaptureSizeMB: 0 });
  if (!autoIntercept) { console.info("MDM capture skipped: disabled"); return false; }
  if (downloadItem.totalBytes >= 0 && minimumCaptureSizeMB > 0 && downloadItem.totalBytes < minimumCaptureSizeMB * 1000000) { console.info("MDM capture skipped: below size threshold"); return false; }
  if (downloadItem.byExtensionId === chrome.runtime.id) { console.info("MDM capture skipped: created by this extension"); return false; }
  if (!/^https?:\/\//i.test(downloadItem.finalUrl || downloadItem.url)) { console.info("MDM capture skipped: unsupported URL"); return false; }
  if (downloadItem.paused) return true;
  try { await chrome.downloads.pause(downloadItem.id); console.info("MDM browser download parked", { id: downloadItem.id }); return true; }
  catch (error) { console.warn("MDM could not park browser download", { id: downloadItem.id, error: String(error) }); return false; }
}

async function decideCapture(downloadItem, parkedByUs) {
  const url = downloadItem.finalUrl || downloadItem.url;
  console.info("MDM handing download to app", { id: downloadItem.id, parkedByUs, filename: downloadItem.filename || "" });
  const accepted = await sendToApp({
    url,
    suggestedFileName: downloadItem.filename?.split(/[\\/]/).pop() || undefined,
    referrer: downloadItem.referrer,
    userAgent: navigator.userAgent,
    cookie: await cookieHeaderFor(url),
    totalBytes: downloadItem.totalBytes
  });
  console.info("MDM app handoff result", { id: downloadItem.id, accepted });
  if (!accepted) {
    if (parkedByUs) { try { await chrome.downloads.resume(downloadItem.id); } catch { /* The browser owns recovery. */ } }
    return;
  }
  // The browser may have resumed or even finished the parked item while the
  // user was reviewing the MDM prompt. Always clean up the browser copy after
  // MDM accepts it; otherwise Edge can leave a duplicate download behind.
  try { await chrome.downloads.pause(downloadItem.id); } catch { /* already stopped */ }
  try { await chrome.downloads.cancel(downloadItem.id); } catch { /* already complete */ }
  try { await chrome.downloads.removeFile(downloadItem.id); } catch { /* no browser file remains */ }
  try { await chrome.downloads.erase({ id: downloadItem.id }); } catch { /* best effort */ }
}

const determiningFilename = chrome.downloads.onDeterminingFilename;
if (determiningFilename) {
  chrome.downloads.onCreated.addListener((downloadItem) => {
    // Keep the promise, not only its eventual boolean result. Edge can fire
    // onDeterminingFilename before an awaited downloads.pause() completes.
    parkedDownloads.set(downloadItem.id, parkDownload(downloadItem));
  });
  determiningFilename.addListener((downloadItem, suggest) => {
    // Let Chrome finish its normal filename resolution before we inspect it.
    suggest();
    const parking = parkedDownloads.get(downloadItem.id) || Promise.resolve(false);
    parkedDownloads.delete(downloadItem.id);
    void parking.then((parkedByUs) => {
      if (parkedByUs) return decideCapture(downloadItem, true);
      console.info("MDM capture skipped: download was not parked");
      return undefined;
    });
  });
} else {
  // Firefox and test shims do not expose onDeterminingFilename. They keep the
  // original one-event flow, with the same safe resume-on-failure behavior.
  chrome.downloads.onCreated.addListener(async (downloadItem) => {
    const parkedByUs = await parkDownload(downloadItem);
    if (parkedByUs) await decideCapture(downloadItem, true);
  });
}

async function cookieHeaderFor(url) {
  try {
    const cookies = await chrome.cookies.getAll({ url });
    return cookies.map((c) => `${c.name}=${c.value}`).join("; ") || undefined;
  } catch { return undefined; }
}

function sendToApp(message) {
  if (!/^https?:\/\//i.test(message.url || "")) return Promise.resolve(false);
  return new Promise((resolve) => {
    let settled = false;
    let port;
    const finish = (accepted, declined = false) => {
      if (settled) return;
      settled = true;
      if (accepted) notifySuccess();
      else if (!declined) notifyFailure();
      else refreshMonitoringBadge();
      resolve(accepted);
      port?.disconnect();
    };
    try {
      // A native port keeps the worker alive while the capture prompt is open.
      port = chrome.runtime.connectNative(NATIVE_HOST);
      port.onMessage.addListener(response => {
        console.info("MDM native host response", { status: response?.status });
        finish(response?.status === "queued", response?.status === "declined");
      });
      port.onDisconnect.addListener(() => {
        const error = chrome.runtime.lastError;
        if (error) console.warn("MDM native host disconnected", error.message);
        finish(false);
      });
      port.postMessage(message);
    } catch { finish(false); }
  });
}

function notifySuccess() {
  chrome.action.setTitle({ title: "Modern Download Manager — download sent" });
  setTimeout(refreshMonitoringBadge, 1500);
}

function notifyFailure() {
  chrome.action.setTitle({ title: "Modern Download Manager — app unavailable" });
  setTimeout(refreshMonitoringBadge, 3000);
}

function refreshMonitoringBadge() {
  chrome.storage.local.get({ autoIntercept: true }, ({ autoIntercept }) => updateMonitoringBadge(autoIntercept));
}

function updateMonitoringBadge(enabled) {
  chrome.action.setBadgeText({ text: "" });
  chrome.action.setIcon({ path: enabled ? {
    16: "icons/icon16.png", 32: "icons/icon32.png", 48: "icons/icon48.png", 128: "icons/icon128.png"
  } : {
    16: "icons/icon16-disabled.png", 32: "icons/icon32-disabled.png", 48: "icons/icon48-disabled.png", 128: "icons/icon128-disabled.png"
  } });
  chrome.action.setTitle({ title: enabled
    ? "Modern Download Manager — monitoring downloads"
    : "Modern Download Manager — monitoring off" });
}
