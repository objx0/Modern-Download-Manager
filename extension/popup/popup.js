const checkbox = document.getElementById("autoIntercept");
const status = document.getElementById("status");
const minimumSize = document.getElementById("minimumCaptureSizeMB");

chrome.storage.local.get({ autoIntercept: true, minimumCaptureSizeMB: 0 }, ({ autoIntercept, minimumCaptureSizeMB }) => {
  checkbox.checked = autoIntercept;
  minimumSize.value = minimumCaptureSizeMB;
});

minimumSize.addEventListener("change", () => {
  const value = Math.max(0, Number(minimumSize.value) || 0);
  minimumSize.value = value;
  chrome.storage.local.set({ minimumCaptureSizeMB: value }, () => {
    status.textContent = "Saved.";
    setTimeout(() => (status.textContent = ""), 1200);
  });
});

checkbox.addEventListener("change", () => {
  chrome.storage.local.set({ autoIntercept: checkbox.checked }, () => {
    status.textContent = "Saved.";
    setTimeout(() => (status.textContent = ""), 1200);
  });
});
