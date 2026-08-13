const checkbox = document.getElementById("autoIntercept");
const status = document.getElementById("status");
const minimumSize = document.getElementById("minimumCaptureSizeMB");

browser.storage.local.get({ autoIntercept: true, minimumCaptureSizeMB: 0 }).then(({ autoIntercept, minimumCaptureSizeMB }) => {
  checkbox.checked = autoIntercept;
  minimumSize.value = minimumCaptureSizeMB;
});

minimumSize.addEventListener("change", () => {
  const value = Math.max(0, Number(minimumSize.value) || 0);
  minimumSize.value = value;
  browser.storage.local.set({ minimumCaptureSizeMB: value }).then(showSaved);
});

checkbox.addEventListener("change", () => browser.storage.local.set({ autoIntercept: checkbox.checked }).then(showSaved));

function showSaved() {
  status.textContent = "Saved.";
  setTimeout(() => (status.textContent = ""), 1200);
}
