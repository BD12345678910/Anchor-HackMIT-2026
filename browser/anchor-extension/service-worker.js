importScripts("adapter-state.js");

const NATIVE_HOST = "com.anchor.desktop";
const DEFAULTS = {
  imageBlur: true,
  threshold: 0.62,
  futureTextMask: false,
  lookahead: 1,
  suppressAnimations: false,
  deniedOrigins: [],
  enabledOrigins: [],
};
let nativePort = null;
let reconnectTimer = null;
let reconnectDelay = 1_000;
let lastToolkitState = {};

async function setConnectionStatus(connected, detail = "") {
  await chrome.storage.local.set({ bridgeStatus: { connected, detail, updatedAt: Date.now() } });
}

function scheduleReconnect() {
  if (reconnectTimer) return;
  reconnectTimer = setTimeout(() => {
    reconnectTimer = null;
    connectNative();
  }, reconnectDelay);
  reconnectDelay = Math.min(30_000, reconnectDelay * 2);
}

function connectNative() {
  if (nativePort) return nativePort;
  try {
    const port = chrome.runtime.connectNative(NATIVE_HOST);
    nativePort = port;
    port.onMessage.addListener(async (message) => {
      if (message?.type === "toolkitState") lastToolkitState = message;
      await broadcastToEnabledTabs(message);
    });
    port.onDisconnect.addListener(() => {
      nativePort = null;
      setConnectionStatus(false, chrome.runtime.lastError?.message ?? "Desktop app disconnected");
      scheduleReconnect();
    });
    reconnectDelay = 1_000;
    setConnectionStatus(true, "Desktop app connected");
    port.postMessage({ type: "hello", protocolVersion: 1, capabilities: ["pageContext", "readingProgress", "imageBlur", "futureTextMask", "animationSuppression", "recoveryAnchor"] });
  } catch (error) {
    nativePort = null;
    setConnectionStatus(false, error?.message ?? "Desktop app unavailable");
    scheduleReconnect();
  }
  return nativePort;
}

async function getSettings() {
  return chrome.storage.sync.get(DEFAULTS);
}

async function updateBadge(tabId, enabled) {
  await chrome.action.setBadgeText({ tabId, text: enabled ? "ON" : "" });
  if (enabled) await chrome.action.setBadgeBackgroundColor({ tabId, color: "#587aef" });
}

async function sendToolkitState(tabId, settings, state = lastToolkitState) {
  for (const message of AnchorAdapterState.messagesForToolkitState(state, settings)) {
    await chrome.tabs.sendMessage(tabId, message);
  }
}

async function enableOnTab(tabId, url) {
  const settings = await getSettings();
  if (!AnchorAdapterState.isOriginEnabled(settings.enabledOrigins, url, settings.deniedOrigins)) {
    await updateBadge(tabId, false);
    return false;
  }
  try {
    await chrome.scripting.executeScript({ target: { tabId }, files: ["content-script.js"] });
    await sendToolkitState(tabId, settings);
    await updateBadge(tabId, true);
    connectNative();
    return true;
  } catch (_error) {
    await updateBadge(tabId, false);
    return false;
  }
}

async function broadcastToEnabledTabs(message) {
  const settings = await getSettings();
  const tabs = await chrome.tabs.query({});
  for (const tab of tabs) {
    if (!tab.id || !AnchorAdapterState.isOriginEnabled(settings.enabledOrigins, tab.url, settings.deniedOrigins)) continue;
    try {
      if (message?.type === "toolkitState") await sendToolkitState(tab.id, settings, message);
      else if (message?.type === "showRecoveryAnchor") await chrome.tabs.sendMessage(tab.id, { command: "showRecoveryAnchor", paragraphIndex: message.paragraphIndex });
      else if (message?.type === "clearInterventions") await chrome.tabs.sendMessage(tab.id, { command: "clearInterventions" });
    } catch (_error) { /* Tab navigated or adapter is not yet injected. */ }
  }
}

chrome.runtime.onInstalled.addListener(async () => {
  const existing = await chrome.storage.sync.get(Object.keys(DEFAULTS));
  await chrome.storage.sync.set({ ...DEFAULTS, ...existing });
  connectNative();
});
chrome.runtime.onStartup.addListener(connectNative);

chrome.action.onClicked.addListener(async (tab) => {
  if (!tab.id) return;
  const origin = AnchorAdapterState.normalizeOrigin(tab.url);
  if (!origin) return;
  const settings = await getSettings();
  const currentlyEnabled = AnchorAdapterState.isOriginEnabled(settings.enabledOrigins, tab.url, settings.deniedOrigins);
  if (currentlyEnabled) {
    const enabledOrigins = AnchorAdapterState.setOriginEnabled(settings.enabledOrigins, origin, false);
    await chrome.storage.sync.set({ enabledOrigins });
    try { await chrome.tabs.sendMessage(tab.id, { command: "clearInterventions" }); } catch (_error) { }
    await updateBadge(tab.id, false);
    return;
  }

  const granted = await chrome.permissions.request({ origins: [`${origin}/*`] });
  if (!granted) return;
  const enabledOrigins = AnchorAdapterState.setOriginEnabled(settings.enabledOrigins, origin, true);
  await chrome.storage.sync.set({ enabledOrigins });
  await enableOnTab(tab.id, tab.url);
});

chrome.tabs.onUpdated.addListener((tabId, changeInfo, tab) => {
  if (changeInfo.status === "complete" && tab.url) enableOnTab(tabId, tab.url);
});
chrome.tabs.onActivated.addListener(async ({ tabId }) => {
  const tab = await chrome.tabs.get(tabId);
  const settings = await getSettings();
  await updateBadge(tabId, AnchorAdapterState.isOriginEnabled(settings.enabledOrigins, tab.url, settings.deniedOrigins));
});

chrome.runtime.onMessage.addListener((message, sender, respond) => {
  if (message?.command === "getAdapterStatus") {
    Promise.all([chrome.storage.local.get("bridgeStatus"), getSettings()]).then(([local, settings]) => respond({
      bridgeStatus: local.bridgeStatus ?? { connected: false, detail: "Not connected yet" },
      enabledOrigins: settings.enabledOrigins,
    }));
    return true;
  }
  if (message?.source !== "anchor-content") return false;
  const payload = {
    ...message,
    tab: { id: sender.tab?.id, origin: sender.origin ?? "", title: sender.tab?.title?.slice(0, 240) ?? "" },
  };
  try { connectNative()?.postMessage(payload); } catch (_error) {
    nativePort = null;
    scheduleReconnect();
  }
  return false;
});

connectNative();
