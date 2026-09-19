(function initializeAnchorFocus(globalScope, factory) {
  const api = factory();
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  globalScope.AnchorFocusEngine = api;
  if (typeof document !== "undefined" && typeof chrome !== "undefined" && chrome.runtime) api.boot();
})(typeof globalThis !== "undefined" ? globalThis : this, function createAnchorFocusEngine() {
  const MASK_CLASS = "anchor-future-mask";
  const FILTERED_ATTRIBUTE = "anchorFiltered";

  function clamp(value) {
    return Number.isFinite(value) ? Math.max(0, Math.min(1, value)) : 0;
  }

  function classifyImage(input) {
    const viewportArea = Math.max(1, input.viewportWidth * input.viewportHeight);
    const area = Math.max(0, input.width * input.height);
    const areaSignal = Math.sqrt(clamp(area / viewportArea));
    const verticalCenter = (input.top + input.height / 2) / Math.max(1, input.viewportHeight);
    const centerSignal = 1 - Math.min(1, Math.abs(verticalCenter - 0.5) * 2);
    return clamp(
      areaSignal * 0.45
      + (input.animated ? 0.2 : 0)
      + clamp(input.contrast ?? 0.5) * 0.1
      + (1 - clamp(input.relevance ?? 0.5)) * 0.15
      + centerSignal * 0.1,
    );
  }

  function isProtectedPage(url, signals = {}) {
    const protocol = String(url ?? "").split(":", 1)[0].toLowerCase();
    if (!["http", "https"].includes(protocol)) return true;
    return Boolean(signals.hasPassword || signals.hasPayment || signals.deniedOrigin);
  }

  function shouldExcludeElement(element) {
    if (!element) return true;
    const tag = String(element.tagName ?? "").toLowerCase();
    const type = String(element.type ?? "").toLowerCase();
    if (tag === "video" || tag === "audio" || tag === "canvas" || tag === "svg") return true;
    if (tag === "input" && ["password", "email", "tel", "number"].includes(type)) return true;
    if (element.isContentEditable) return true;
    return Boolean(element.closest?.("form, video, audio, [contenteditable='true'], [role='dialog']"));
  }

  function inferAnimation(image) {
    const source = String(image.currentSrc ?? image.src ?? "").toLowerCase();
    return source.endsWith(".gif") || image.dataset?.animated === "true";
  }

  function applyImageFiltering(root, options = {}) {
    const viewportWidth = options.viewportWidth ?? globalScope.innerWidth ?? 1;
    const viewportHeight = options.viewportHeight ?? globalScope.innerHeight ?? 1;
    const threshold = clamp(options.threshold ?? 0.62);
    const relevance = options.relevance ?? (() => 0.5);
    let changed = 0;
    for (const image of root.querySelectorAll("img")) {
      if (shouldExcludeElement(image)) continue;
      const rectangle = image.getBoundingClientRect();
      if (rectangle.width <= 0 || rectangle.height <= 0) continue;
      const score = classifyImage({
        width: rectangle.width,
        height: rectangle.height,
        viewportWidth,
        viewportHeight,
        animated: inferAnimation(image),
        contrast: Number(image.dataset?.contrast ?? 0.5),
        relevance: relevance(image),
        top: rectangle.top,
      });
      image.dataset.anchorDistractionScore = score.toFixed(3);
      if (score < threshold || image.dataset[FILTERED_ATTRIBUTE] === "true") continue;
      image.dataset.anchorOriginalFilter = image.style.filter ?? "";
      image.dataset[FILTERED_ATTRIBUTE] = "true";
      image.classList.add("anchor-image-filtered");
      image.style.filter = `${image.style.filter ? `${image.style.filter} ` : ""}blur(12px) saturate(0.35)`;
      image.style.transition = "filter 160ms ease";
      changed += 1;
    }
    return changed;
  }

  function paragraphCandidates(root) {
    return [...root.querySelectorAll("p, article li, main li")].filter((element) => !shouldExcludeElement(element));
  }

  function maskFutureText(root, currentIndex, options = {}) {
    const paragraphs = paragraphCandidates(root);
    const firstMasked = Math.max(0, currentIndex + 1 + Math.max(0, options.lookahead ?? 1));
    let masked = 0;
    paragraphs.forEach((paragraph, index) => {
      if (index >= firstMasked) {
        if (!paragraph.classList.contains(MASK_CLASS)) masked += 1;
        paragraph.classList.add(MASK_CLASS);
      } else {
        paragraph.classList.remove(MASK_CLASS);
      }
    });
    return masked;
  }

  function clearInterventions(root) {
    for (const element of root.querySelectorAll(".anchor-image-filtered, .anchor-future-mask, .anchor-recovery-anchor, [data-anchor-filtered='true']")) {
      if (element.dataset?.[FILTERED_ATTRIBUTE] === "true") {
        element.style.filter = element.dataset.anchorOriginalFilter ?? "";
        element.style.transition = "";
        delete element.dataset.anchorOriginalFilter;
        delete element.dataset[FILTERED_ATTRIBUTE];
        delete element.dataset.anchorDistractionScore;
      }
      element.classList.remove("anchor-image-filtered", MASK_CLASS, "anchor-recovery-anchor");
    }
  }

  function createReadingTracker(emit, options = {}) {
    const skipThreshold = clamp(options.skipThreshold ?? 0.28);
    const dwellThreshold = Math.max(2, options.dwellThreshold ?? 4);
    let lastProgress = null;
    let phrase = "";
    let phraseCount = 0;
    let phraseEmitted = false;
    return {
      observeProgress(progress, timestamp = Date.now()) {
        const normalized = clamp(progress);
        if (lastProgress !== null && normalized - lastProgress >= skipThreshold) {
          emit({ type: "reading-skip", from: lastProgress, to: normalized, timestamp });
        }
        lastProgress = normalized;
      },
      observePhrase(value, timestamp = Date.now()) {
        const normalized = String(value ?? "").trim().replace(/\s+/g, " ").slice(0, 180);
        if (!normalized) return;
        if (normalized === phrase) phraseCount += 1;
        else {
          phrase = normalized;
          phraseCount = 1;
          phraseEmitted = false;
        }
        if (!phraseEmitted && phraseCount >= dwellThreshold) {
          phraseEmitted = true;
          emit({ type: "stuck-phrase", phrase, timestamp });
        }
      },
      snapshot() { return { progress: lastProgress ?? 0, phrase, phraseCount }; },
    };
  }

  function injectStyles() {
    if (document.getElementById("anchor-focus-styles")) return;
    const style = document.createElement("style");
    style.id = "anchor-focus-styles";
    style.textContent = `
      .anchor-image-filtered:hover { filter: none !important; }
      .anchor-future-mask { color: transparent !important; text-shadow: 0 0 10px rgba(120,130,150,.75); user-select: none; }
      .anchor-future-mask:hover { color: inherit !important; text-shadow: none; user-select: text; }
      .anchor-recovery-anchor { outline: 3px solid #7c9cff !important; outline-offset: 6px; border-radius: 4px; }
    `;
    document.documentElement.appendChild(style);
  }

  function boot() {
    if (globalScope.__anchorFocusBooted) return;
    globalScope.__anchorFocusBooted = true;
    const signals = {
      hasPassword: Boolean(document.querySelector("input[type='password']")),
      hasPayment: Boolean(document.querySelector("[autocomplete^='cc-'], input[name*='card' i]")),
    };
    if (isProtectedPage(location.href, signals)) return;
    injectStyles();
    const tracker = createReadingTracker((event) => chrome.runtime.sendMessage({ source: "anchor-content", event }));
    let settings = { imageBlur: true, threshold: 0.62, futureTextMask: false, lookahead: 1 };
    let currentParagraph = 0;

    const apply = () => {
      if (settings.imageBlur) applyImageFiltering(document, { threshold: settings.threshold });
      if (settings.futureTextMask) maskFutureText(document, currentParagraph, settings);
    };
    const observer = new MutationObserver(() => apply());
    observer.observe(document.documentElement, { childList: true, subtree: true });
    addEventListener("scroll", () => {
      const maximum = Math.max(1, document.documentElement.scrollHeight - innerHeight);
      tracker.observeProgress(scrollY / maximum);
      const paragraphs = paragraphCandidates(document);
      const index = paragraphs.findIndex((item) => item.getBoundingClientRect().bottom > innerHeight * 0.45);
      currentParagraph = index < 0 ? Math.max(0, paragraphs.length - 1) : index;
      if (settings.futureTextMask) maskFutureText(document, currentParagraph, settings);
    }, { passive: true });
    addEventListener("pointerover", (event) => {
      const text = event.target?.textContent?.trim();
      if (text && text.length <= 180) tracker.observePhrase(text);
    }, { passive: true });

    chrome.runtime.onMessage.addListener((message, _sender, respond) => {
      if (message?.command === "setVisualFilter") {
        settings = { ...settings, imageBlur: true, threshold: message.threshold ?? settings.threshold };
        apply();
      } else if (message?.command === "setFutureTextMask") {
        settings = { ...settings, futureTextMask: Boolean(message.enabled), lookahead: message.lookahead ?? settings.lookahead };
        if (settings.futureTextMask) apply(); else clearInterventions(document);
      } else if (message?.command === "clearInterventions") {
        clearInterventions(document);
      } else if (message?.command === "showRecoveryAnchor") {
        const paragraphs = paragraphCandidates(document);
        const anchor = paragraphs[Math.max(0, Math.min(paragraphs.length - 1, message.paragraphIndex ?? currentParagraph))];
        anchor?.classList.add("anchor-recovery-anchor");
        anchor?.scrollIntoView?.({ behavior: "smooth", block: "center" });
      }
      respond?.({ ok: true, tracker: tracker.snapshot() });
    });
    chrome.storage.sync.get(["imageBlur", "threshold", "futureTextMask", "lookahead"], (saved) => {
      settings = { ...settings, ...saved };
      apply();
    });
    chrome.runtime.sendMessage({
      source: "anchor-content",
      event: { type: "page-context", origin: location.origin, title: document.title.slice(0, 240) },
    });
  }

  return {
    classifyImage,
    isProtectedPage,
    shouldExcludeElement,
    applyImageFiltering,
    maskFutureText,
    clearInterventions,
    createReadingTracker,
    boot,
  };
});
