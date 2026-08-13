const NATIVE_HOST = "com.moderndownloadmanager.host";
const MENU_ID = "send-to-mdm";

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.create({ id: MENU_ID, title: "Download with Modern Download Manager", contexts: ["link", "video", "audio", "image"] });
  chrome.storage.local.get({ autoIntercept: true, minimumCaptureSizeMB: 0 }, (settings) => chrome.storage.local.set(settings));
});

chrome.contextMenus.onClicked.addListener(async (info, tab) => {
  if (info.menuItemId !== MENU_ID) return;
  const url = info.srcUrl || info.linkUrl;
  if (!url) return;
  await sendToApp({ url, referrer: tab?.url, userAgent: navigator.userAgent, cookie: await cookieHeaderFor(url) });
});

chrome.downloads.onCreated.addListener(async (downloadItem) => {
  const { autoIntercept, minimumCaptureSizeMB } = await chrome.storage.local.get({ autoIntercept: true, minimumCaptureSizeMB: 0 });
  if (!autoIntercept || (downloadItem.totalBytes >= 0 && minimumCaptureSizeMB > 0 && downloadItem.totalBytes < minimumCaptureSizeMB * 1000000)) return;
  if (downloadItem.byExtensionId === chrome.runtime.id) return;

  // Deliver first. If the host/app is unavailable, leave the browser download
  // alive so a failed integration can never lose the user's download.
  const accepted = await sendToApp({
    url: downloadItem.finalUrl || downloadItem.url,
    suggestedFileName: downloadItem.filename?.split(/[\\/]/).pop() || undefined,
    referrer: downloadItem.referrer,
    userAgent: navigator.userAgent,
    cookie: await cookieHeaderFor(downloadItem.finalUrl || downloadItem.url),
    totalBytes: downloadItem.totalBytes
  });
  if (!accepted) return;
  try { await chrome.downloads.cancel(downloadItem.id); } catch { return; }
  try { await chrome.downloads.erase({ id: downloadItem.id }); } catch { /* best effort */ }
});

async function cookieHeaderFor(url) {
  try {
    const cookies = await chrome.cookies.getAll({ url });
    return cookies.map((c) => `${c.name}=${c.value}`).join("; ") || undefined;
  } catch { return undefined; }
}

function sendToApp(message) {
  return new Promise((resolve) => {
    chrome.runtime.sendNativeMessage(NATIVE_HOST, message, (response) => {
      if (chrome.runtime.lastError) {
        console.warn("Modern Download Manager: native host error", chrome.runtime.lastError.message);
        notifyFailure();
        resolve(false);
        return;
      }
      const accepted = response?.status === "queued";
      accepted ? notifySuccess() : notifyFailure();
      resolve(accepted);
    });
  });
}

function notifySuccess() {
  chrome.action.setBadgeText({ text: "OK" });
  chrome.action.setBadgeBackgroundColor({ color: "#2e7d32" });
  setTimeout(() => chrome.action.setBadgeText({ text: "" }), 1500);
}

function notifyFailure() {
  chrome.action.setBadgeText({ text: "!" });
  chrome.action.setBadgeBackgroundColor({ color: "#c62828" });
  setTimeout(() => chrome.action.setBadgeText({ text: "" }), 3000);
}
