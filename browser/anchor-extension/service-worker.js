const NATIVE_HOST = "com.anchor.desktop";
let nativePort = null;

function connectNative() {
  if (nativePort) return nativePort;
  try {
    nativePort = chrome.runtime.connectNative(NATIVE_HOST);
    nativePort.onMessage.addListener((message) => broadcastToActiveTab(message));
    nativePort.onDisconnect.addListener(() => { nativePort = null; });
  } catch (_error) {
    nativePort = null;
  }
  return nativePort;
}

async function broadcastToActiveTab(message) {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (!tab?.id) return;
  try { await chrome.tabs.sendMessage(tab.id, message); } catch (_error) { /* Adapter is opt-in per tab. */ }
}

chrome.runtime.onInstalled.addListener(() => {
  chrome.storage.sync.set({ imageBlur: true, threshold: 0.62, futureTextMask: false, lookahead: 1, deniedOrigins: [] });
});

chrome.action.onClicked.addListener(async (tab) => {
  if (!tab.id || !/^https?:/i.test(tab.url ?? "")) return;
  try {
    await chrome.scripting.executeScript({ target: { tabId: tab.id }, files: ["content-script.js"] });
    const saved = await chrome.storage.sync.get(["imageBlur", "threshold", "futureTextMask", "lookahead", "deniedOrigins"]);
    const origin = new URL(tab.url).origin;
    if ((saved.deniedOrigins ?? []).includes(origin)) {
      await chrome.tabs.sendMessage(tab.id, { command: "clearInterventions" });
      return;
    }
    await chrome.tabs.sendMessage(tab.id, { command: "setVisualFilter", threshold: saved.threshold });
    await chrome.tabs.sendMessage(tab.id, { command: "setFutureTextMask", enabled: saved.futureTextMask, lookahead: saved.lookahead });
    connectNative();
  } catch (_error) {
    // Protected browser pages and policy-blocked tabs intentionally fail closed.
  }
});

chrome.runtime.onMessage.addListener((message, sender) => {
  if (message?.source !== "anchor-content") return;
  const payload = {
    ...message,
    tab: { id: sender.tab?.id, origin: sender.origin ?? "", title: sender.tab?.title?.slice(0, 240) ?? "" },
  };
  try { connectNative()?.postMessage(payload); } catch (_error) { nativePort = null; }
});
