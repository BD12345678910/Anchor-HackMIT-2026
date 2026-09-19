const fields = {
  imageBlur: document.querySelector("#imageBlur"),
  threshold: document.querySelector("#threshold"),
  futureTextMask: document.querySelector("#futureTextMask"),
  lookahead: document.querySelector("#lookahead"),
  deniedOrigins: document.querySelector("#deniedOrigins"),
};
const thresholdValue = document.querySelector("#thresholdValue");
const status = document.querySelector("#status");

async function load() {
  const saved = await chrome.storage.sync.get({ imageBlur: true, threshold: 0.62, futureTextMask: false, lookahead: 1, deniedOrigins: [] });
  fields.imageBlur.checked = saved.imageBlur;
  fields.threshold.value = saved.threshold;
  fields.futureTextMask.checked = saved.futureTextMask;
  fields.lookahead.value = saved.lookahead;
  fields.deniedOrigins.value = saved.deniedOrigins.join("\n");
  thresholdValue.textContent = Number(saved.threshold).toFixed(2);
}

fields.threshold.addEventListener("input", () => { thresholdValue.textContent = Number(fields.threshold.value).toFixed(2); });
document.querySelector("#save").addEventListener("click", async () => {
  const deniedOrigins = fields.deniedOrigins.value.split(/\r?\n/).map((item) => item.trim()).filter((item) => /^https?:\/\//i.test(item));
  await chrome.storage.sync.set({
    imageBlur: fields.imageBlur.checked,
    threshold: Number(fields.threshold.value),
    futureTextMask: fields.futureTextMask.checked,
    lookahead: Math.max(0, Math.min(8, Number(fields.lookahead.value))),
    deniedOrigins,
  });
  status.textContent = "Saved";
  setTimeout(() => { status.textContent = ""; }, 1500);
});

load();
