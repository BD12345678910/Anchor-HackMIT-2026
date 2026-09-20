(function initializeAnchorFocus(globalScope, factory) {
  const api = factory();
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  globalScope.AnchorFocusEngine = api;
  if (typeof document !== "undefined" && typeof chrome !== "undefined" && chrome.runtime) api.boot();
})(typeof globalThis !== "undefined" ? globalThis : this, function createAnchorFocusEngine() {
  const MASK_CLASS = "anchor-future-mask";
  const FILTERED_ATTRIBUTE = "anchorFiltered";
  const ICON_LIMIT_PX = 160;
  const DEFAULT_THRESHOLD = 0.5;

  function clamp(value) {
    return Number.isFinite(value) ? Math.max(0, Math.min(1, value)) : 0;
  }

  function classifyImage(input) {
    const viewportArea = Math.max(1, input.viewportWidth * input.viewportHeight);
    const area = Math.max(0, input.width * input.height);
    const areaSignal = Math.min(1, Math.sqrt(clamp(area / viewportArea)) * 2.2);
    const pixelSignal = clamp(Math.min(input.width, input.height) / ICON_LIMIT_PX);
    const verticalCenter = (input.top + input.height / 2) / Math.max(1, input.viewportHeight);
    const centerSignal = 1 - Math.min(1, Math.abs(verticalCenter - 0.5) * 2);
    return clamp(
      areaSignal * 0.4
      + pixelSignal * 0.25
      + (input.animated ? 0.15 : 0)
      + clamp(input.contrast ?? 0.5) * 0.05
      + (1 - clamp(input.relevance ?? 0.5)) * 0.1
      + centerSignal * 0.05,
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

  function inferAnimation(element) {
    const source = String(element.currentSrc ?? element.src ?? element.style?.backgroundImage ?? "").toLowerCase();
    return source.includes(".gif") || element.dataset?.animated === "true" || element.dataset?.anchorAnimated === "true";
  }

  function applyImageFiltering(root, options = {}) {
    const viewportWidth = options.viewportWidth ?? globalScope.innerWidth ?? 1;
    const viewportHeight = options.viewportHeight ?? globalScope.innerHeight ?? 1;
    const threshold = clamp(options.threshold ?? DEFAULT_THRESHOLD);
    const relevance = options.relevance ?? (() => 0.5);
    let changed = 0;
    const candidates = [...new Set([
      ...root.querySelectorAll("img"),
      ...root.querySelectorAll("[style*='background-image']"),
    ])];
    for (const image of candidates) {
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

  function clearFutureTextMasks(root) {
    let changed = 0;
    for (const paragraph of root.querySelectorAll(`.${MASK_CLASS}`)) {
      paragraph.classList.remove(MASK_CLASS);
      changed += 1;
    }
    return changed;
  }

  function clearImageFilters(root) {
    let changed = 0;
    for (const element of root.querySelectorAll(".anchor-image-filtered, [data-anchor-filtered='true']")) {
      if (element.dataset?.[FILTERED_ATTRIBUTE] !== "true") continue;
      element.style.filter = element.dataset.anchorOriginalFilter ?? "";
      element.style.transition = "";
      delete element.dataset.anchorOriginalFilter;
      delete element.dataset[FILTERED_ATTRIBUTE];
      delete element.dataset.anchorDistractionScore;
      element.classList.remove("anchor-image-filtered");
      changed += 1;
    }
    return changed;
  }

  function applyAnimationSuppression(root, enabled) {
    let changed = 0;
    for (const element of root.querySelectorAll("[data-anchor-animated='true'], [style*='animation']")) {
      if (shouldExcludeElement(element)) continue;
      if (enabled && element.dataset.anchorAnimationSuppressed !== "true") {
        element.dataset.anchorOriginalAnimationPlayState = element.style.animationPlayState ?? "";
        element.dataset.anchorAnimationSuppressed = "true";
        element.style.animationPlayState = "paused";
        changed += 1;
      } else if (!enabled && element.dataset.anchorAnimationSuppressed === "true") {
        element.style.animationPlayState = element.dataset.anchorOriginalAnimationPlayState ?? "";
        delete element.dataset.anchorOriginalAnimationPlayState;
        delete element.dataset.anchorAnimationSuppressed;
        changed += 1;
      }
    }
    return changed;
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
    applyAnimationSuppression(root, false);
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
    let settings = { imageBlur: true, threshold: DEFAULT_THRESHOLD, futureTextMask: false, lookahead: 1, suppressAnimations: false };
    let currentParagraph = 0;
    let lastProgressSentAt = 0;

    const apply = () => {
      if (settings.imageBlur) applyImageFiltering(document, { threshold: settings.threshold });
      if (settings.futureTextMask) maskFutureText(document, currentParagraph, settings);
      applyAnimationSuppression(document, settings.suppressAnimations);
    };
    let mutationTimer = null;
    const observer = new MutationObserver(() => {
      clearTimeout(mutationTimer);
      mutationTimer = setTimeout(apply, 80);
    });
    observer.observe(document.documentElement, { childList: true, subtree: true });
    addEventListener("scroll", () => {
      const maximum = Math.max(1, document.documentElement.scrollHeight - innerHeight);
      tracker.observeProgress(scrollY / maximum);
      const paragraphs = paragraphCandidates(document);
      const index = paragraphs.findIndex((item) => item.getBoundingClientRect().bottom > innerHeight * 0.45);
      currentParagraph = index < 0 ? Math.max(0, paragraphs.length - 1) : index;
      const now = Date.now();
      if (now - lastProgressSentAt >= 250) {
        lastProgressSentAt = now;
        chrome.runtime.sendMessage({
          source: "anchor-content",
          event: { type: "reading-progress", progress: scrollY / maximum, paragraphIndex: currentParagraph, timestamp: now },
        });
      }
      if (settings.futureTextMask) maskFutureText(document, currentParagraph, settings);
    }, { passive: true });
    addEventListener("pointerover", (event) => {
      const text = event.target?.textContent?.trim();
      if (text && text.length <= 180) tracker.observePhrase(text);
    }, { passive: true });

    chrome.runtime.onMessage.addListener((message, _sender, respond) => {
      if (message?.command === "setVisualFilter") {
        settings = { ...settings, imageBlur: message.enabled !== false, threshold: message.threshold ?? settings.threshold };
        if (settings.imageBlur) apply(); else clearImageFilters(document);
      } else if (message?.command === "setFutureTextMask") {
        settings = { ...settings, futureTextMask: Boolean(message.enabled), lookahead: message.lookahead ?? settings.lookahead };
        if (settings.futureTextMask) apply(); else clearFutureTextMasks(document);
      } else if (message?.command === "setAnimationSuppression") {
        settings = { ...settings, suppressAnimations: Boolean(message.enabled) };
        applyAnimationSuppression(document, settings.suppressAnimations);
      } else if (message?.command === "clearInterventions") {
        clearInterventions(document);
      } else if (message?.command === "showRecoveryAnchor") {
        const paragraphs = paragraphCandidates(document);
        const anchor = paragraphs[Math.max(0, Math.min(paragraphs.length - 1, message.paragraphIndex ?? currentParagraph))];
        anchor?.classList.add("anchor-recovery-anchor");
        anchor?.scrollIntoView?.({ behavior: "smooth", block: "center" });
      }
      respond?.({ ok: true, tracker: tracker.snapshot(), capabilities: ["imageBlur", "futureTextMask", "animationSuppression", "recoveryAnchor"] });
    });
    chrome.storage.sync.get(["imageBlur", "threshold", "futureTextMask", "lookahead", "suppressAnimations"], (saved) => {
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
    clearImageFilters,
    maskFutureText,
    clearFutureTextMasks,
    applyAnimationSuppression,
    clearInterventions,
    createReadingTracker,
    boot,
  };
});
