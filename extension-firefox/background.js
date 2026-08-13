const NATIVE_HOST = "com.moderndownloadmanager.host";
const MENU_ID = "send-to-mdm";

browser.runtime.onInstalled.addListener(() => {
  browser.contextMenus.create({ id: MENU_ID, title: "Download with Modern Download Manager", contexts: ["link", "video", "audio", "image"] });
  browser.storage.local.get({ autoIntercept: true, minimumCaptureSizeMB: 0 }).then((settings) => browser.storage.local.set(settings));
});

browser.contextMenus.onClicked.addListener(async (info, tab) => {
  if (info.menuItemId !== MENU_ID) return;
  const url = info.srcUrl || info.linkUrl;
  if (!url) return;
  await sendToApp({ url, referrer: tab?.url, userAgent: navigator.userAgent, cookie: await cookieHeaderFor(url) });
});

browser.downloads.onCreated.addListener(async (downloadItem) => {
  const { autoIntercept, minimumCaptureSizeMB } = await browser.storage.local.get({ autoIntercept: true, minimumCaptureSizeMB: 0 });
  if (!autoIntercept || (downloadItem.totalBytes >= 0 && minimumCaptureSizeMB > 0 && downloadItem.totalBytes < minimumCaptureSizeMB * 1000000)) return;
  const url = downloadItem.finalUrl || downloadItem.url;
  const accepted = await sendToApp({ url, suggestedFileName: downloadItem.filename?.split(/[\\/]/).pop() || undefined, referrer: downloadItem.referrer, userAgent: navigator.userAgent, cookie: await cookieHeaderFor(url), totalBytes: downloadItem.totalBytes });
  if (!accepted) return;
  try { await browser.downloads.cancel(downloadItem.id); } catch { return; }
  try { await browser.downloads.erase({ id: downloadItem.id }); } catch { /* best effort */ }
});

async function cookieHeaderFor(url) {
  try {
    const cookies = await browser.cookies.getAll({ url });
    return cookies.map((c) => `${c.name}=${c.value}`).join("; ") || undefined;
  } catch { return undefined; }
}

async function sendToApp(message) {
  try {
    const response = await browser.runtime.sendNativeMessage(NATIVE_HOST, message);
    const accepted = response?.status === "queued";
    accepted ? notifySuccess() : notifyFailure();
    return accepted;
  } catch (error) {
    console.warn("Modern Download Manager: native host error", error);
    notifyFailure();
    return false;
  }
}

function notifySuccess() {
  browser.action?.setBadgeText({ text: "OK" });
  browser.action?.setBadgeBackgroundColor({ color: "#2e7d32" });
  setTimeout(() => browser.action?.setBadgeText({ text: "" }), 1500);
}

function notifyFailure() {
  browser.action?.setBadgeText({ text: "!" });
  browser.action?.setBadgeBackgroundColor({ color: "#c62828" });
  setTimeout(() => browser.action?.setBadgeText({ text: "" }), 3000);
}
