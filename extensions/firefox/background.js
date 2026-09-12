const NATIVE_HOST = "com.moderndownloadmanager.host";
const MENU_ID = "send-to-mdm";

browser.runtime.onInstalled.addListener(() => {
  browser.contextMenus.create({ id: MENU_ID, title: "Download with Modern Download Manager", contexts: ["link", "video", "audio", "image"] });
  browser.storage.local.get({ autoIntercept: true, minimumCaptureSizeMB: 0 }).then((settings) => {
    browser.storage.local.set(settings);
    updateMonitoringBadge(settings.autoIntercept);
  });
});

browser.storage.onChanged.addListener((changes, areaName) => {
  if (areaName === "local" && changes.autoIntercept)
    updateMonitoringBadge(changes.autoIntercept.newValue === true);
});

browser.storage.local.get({ autoIntercept: true }).then(({ autoIntercept }) => updateMonitoringBadge(autoIntercept));

browser.contextMenus.onClicked.addListener(async (info, tab) => {
  if (info.menuItemId !== MENU_ID) return;
  const url = info.srcUrl || info.linkUrl;
  if (!url) return;
  await sendToApp({ url, referrer: tab?.url, userAgent: navigator.userAgent, cookie: await cookieHeaderFor(url) });
});

browser.downloads.onCreated.addListener(async (downloadItem) => {
  const { autoIntercept, minimumCaptureSizeMB } = await browser.storage.local.get({ autoIntercept: true, minimumCaptureSizeMB: 0 });
  if (!autoIntercept || (downloadItem.totalBytes >= 0 && minimumCaptureSizeMB > 0 && downloadItem.totalBytes < minimumCaptureSizeMB * 1000000)) return;
  if (downloadItem.byExtensionId === browser.runtime.id) return;
  if (!/^https?:\/\//i.test(downloadItem.finalUrl || downloadItem.url)) return;
  let pausedByUs = false;
  if (downloadItem.paused) pausedByUs = true;
  if (!downloadItem.paused) {
    try { await browser.downloads.pause(downloadItem.id); pausedByUs = true; } catch { /* It may already be complete. */ }
  }
  if (!pausedByUs) return;
  const url = downloadItem.finalUrl || downloadItem.url;
  const accepted = await sendToApp({ url, suggestedFileName: downloadItem.filename?.split(/[\\/]/).pop() || undefined, referrer: downloadItem.referrer, userAgent: navigator.userAgent, cookie: await cookieHeaderFor(url), totalBytes: downloadItem.totalBytes });
  if (!accepted) {
    if (pausedByUs) { try { await browser.downloads.resume(downloadItem.id); } catch { /* The browser owns recovery. */ } }
    return;
  }
  try { await browser.downloads.pause(downloadItem.id); } catch { /* already stopped */ }
  try { await browser.downloads.cancel(downloadItem.id); } catch { /* already complete */ }
  try { await browser.downloads.removeFile(downloadItem.id); } catch { /* no browser file remains */ }
  try { await browser.downloads.erase({ id: downloadItem.id }); } catch { /* best effort */ }
});

async function cookieHeaderFor(url) {
  try {
    const cookies = await browser.cookies.getAll({ url });
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
      port = browser.runtime.connectNative(NATIVE_HOST);
      port.onMessage.addListener(response => finish(response?.status === "queued", response?.status === "declined"));
      port.onDisconnect.addListener(() => { void browser.runtime.lastError; finish(false); });
      port.postMessage(message);
    } catch { finish(false); }
  });
}

function notifySuccess() {
  browser.action?.setTitle({ title: "Modern Download Manager — download sent" });
  setTimeout(refreshMonitoringBadge, 1500);
}

function notifyFailure() {
  browser.action?.setTitle({ title: "Modern Download Manager — app unavailable" });
  setTimeout(refreshMonitoringBadge, 3000);
}

function refreshMonitoringBadge() {
  browser.storage.local.get({ autoIntercept: true }).then(({ autoIntercept }) => updateMonitoringBadge(autoIntercept));
}

function updateMonitoringBadge(enabled) {
  browser.action?.setBadgeText({ text: "" });
  browser.action?.setIcon({ path: enabled ? {
    16: "icons/icon16.png", 32: "icons/icon32.png", 48: "icons/icon48.png", 128: "icons/icon128.png"
  } : {
    16: "icons/icon16-disabled.png", 32: "icons/icon32-disabled.png", 48: "icons/icon48-disabled.png", 128: "icons/icon128-disabled.png"
  } });
  browser.action?.setTitle({ title: enabled
    ? "Modern Download Manager — monitoring downloads"
    : "Modern Download Manager — monitoring off" });
}
