const fields = {
  imageBlur: document.querySelector("#imageBlur"),
  threshold: document.querySelector("#threshold"),
  futureTextMask: document.querySelector("#futureTextMask"),
  suppressAnimations: document.querySelector("#suppressAnimations"),
  lookahead: document.querySelector("#lookahead"),
  deniedOrigins: document.querySelector("#deniedOrigins"),
};
const thresholdValue = document.querySelector("#thresholdValue");
const status = document.querySelector("#status");

async function load() {
  const saved = await chrome.storage.sync.get({ imageBlur: true, threshold: 0.5, futureTextMask: false, suppressAnimations: false, lookahead: 1, deniedOrigins: [] });
  fields.imageBlur.checked = saved.imageBlur;
  fields.threshold.value = saved.threshold;
  fields.futureTextMask.checked = saved.futureTextMask;
  fields.suppressAnimations.checked = saved.suppressAnimations;
  fields.lookahead.value = saved.lookahead;
  fields.deniedOrigins.value = saved.deniedOrigins.join("\n");
  thresholdValue.textContent = Number(saved.threshold).toFixed(2);
  chrome.runtime.sendMessage({ command: "getAdapterStatus" }, (adapter) => {
    const bridge = document.querySelector("#bridgeStatus");
    bridge.textContent = adapter?.bridgeStatus?.connected
      ? "Connected to Anchor desktop"
      : `Not connected — ${adapter?.bridgeStatus?.detail ?? "open the Anchor desktop app"}`;
    bridge.className = adapter?.bridgeStatus?.connected ? "connected" : "disconnected";
    const list = document.querySelector("#enabledOrigins");
    const origins = adapter?.enabledOrigins ?? [];
    list.replaceChildren(...(origins.length
      ? origins.map((origin) => {
        const item = document.createElement("li");
        item.textContent = origin;
        return item;
      })
      : [Object.assign(document.createElement("li"), { textContent: "No sites enabled yet. Click the Anchor browser icon on a page to enable it." })]));
  });
}

fields.threshold.addEventListener("input", () => { thresholdValue.textContent = Number(fields.threshold.value).toFixed(2); });
document.querySelector("#save").addEventListener("click", async () => {
  const deniedOrigins = fields.deniedOrigins.value.split(/\r?\n/).map((item) => item.trim()).filter((item) => /^https?:\/\//i.test(item));
  await chrome.storage.sync.set({
    imageBlur: fields.imageBlur.checked,
    threshold: Number(fields.threshold.value),
    futureTextMask: fields.futureTextMask.checked,
    suppressAnimations: fields.suppressAnimations.checked,
    lookahead: Math.max(0, Math.min(8, Number(fields.lookahead.value))),
    deniedOrigins,
  });
  status.textContent = "Saved";
  setTimeout(() => { status.textContent = ""; }, 1500);
});

load();
