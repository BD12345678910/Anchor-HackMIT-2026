/**
 * The page editing engine Anchor injects over the DevTools protocol: picture downscaling,
 * off-task block removal, sentence rewriting and animation suppression, each reversible.
 * It touches nothing on its own; `page-agent.js` drives it.
 */
(function initializeAnchorFocus(globalScope, factory) {
  const api = factory();
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  globalScope.AnchorFocusEngine = api;
})(typeof globalThis !== "undefined" ? globalThis : this, function createAnchorFocusEngine() {
  const MASK_CLASS = "anchor-future-mask";
  const FILTERED_ATTRIBUTE = "anchorFiltered";
  const REMOVED_ATTRIBUTE = "anchorRemoved";
  const ICON_LIMIT_PX = 160;
  const DEFAULT_THRESHOLD = 0.5;
  const DEFAULT_MAX_WORDS = 28;
  const DEFAULT_PIXEL_FACTOR = 12;
  const SENTENCE_PATTERN = /[^.!?]+[.!?]*/g;
  const CLUTTER_SELECTORS = [
    "aside",
    "[role='complementary']",
    "[id*='comment' i]",
    "[class*='comment' i]",
    "[id*='recommend' i]",
    "[class*='recommend' i]",
    "[class*='related' i]",
    "[class*='sidebar' i]",
    "[id*='sidebar' i]",
    "[class*='promo' i]",
    "[class*='newsletter' i]",
    "[class*='trending' i]",
    "[id*='ad-' i]",
    "[class*='advert' i]",
    "[aria-label*='advertisement' i]",
    "ins.adsbygoogle",
    "iframe[src*='doubleclick']",
    "iframe[src*='googlesyndication']",
    "#secondary",
    "ytd-watch-next-secondary-results-renderer",
  ];

  const PROMO_PATTERN = /(advert|sponsor|promo|newsletter|trending|recommend|related|suggested|subscribe|shop|deal|banner)/i;
  const PROMO_CONTAINERS = "aside, [role='complementary'], [class*='promo' i], [class*='advert' i], [id*='ad-' i],"
    + " [class*='recommend' i], [class*='related' i], [class*='trending' i], [class*='newsletter' i], [class*='sidebar' i]";

  function clamp(value) {
    return Number.isFinite(value) ? Math.max(0, Math.min(1, value)) : 0;
  }

  function textAround(image) {
    return [
      image.alt,
      image.title,
      image.getAttribute?.("aria-label"),
      image.closest?.("figure")?.textContent,
      image.currentSrc ?? image.src,
    ].filter(Boolean).join(" ").toLowerCase();
  }

  /**
   * Only positive evidence degrades a picture: promotional wording or an ad/recommendation
   * container. A picture in the article is left alone even when its caption does not repeat the
   * goal's words — it is usually the very thing being studied.
   */
  function isOffTaskImage(image) {
    const marks = [image.className, image.id, textAround(image)].filter(Boolean).join(" ");
    return Boolean(PROMO_PATTERN.test(marks) || image.closest?.(PROMO_CONTAINERS));
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
    const offTask = options.offTask ?? isOffTaskImage;
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
      if (!offTask(image)) continue;
      if (score < threshold || image.dataset[FILTERED_ATTRIBUTE] === "true") continue;
      if (options.rewriteSource
        && downscaleImageSource(image, { factor: options.pixelFactor, createCanvas: options.createCanvas })) {
        changed += 1;
        continue;
      }
      image.dataset.anchorOriginalFilter = image.style.filter ?? "";
      image.dataset[FILTERED_ATTRIBUTE] = "true";
      image.classList.add("anchor-image-filtered");
      image.style.filter = `${image.style.filter ? `${image.style.filter} ` : ""}blur(12px) saturate(0.35)`;
      image.style.transition = "filter 160ms ease";
      changed += 1;
    }
    return changed;
  }

  /**
   * Deletes the page furniture that has nothing to do with the task (ads, recommendation rails,
   * comment threads, sidebars) instead of only dimming it. Blocks whose text mentions the task
   * keywords stay. The emptied block keeps its measured height so the surrounding layout does not
   * jump, and its markup is parked in a data attribute for `restoreRemovedElements`.
   */
  function removeOffTaskElements(root, options = {}) {
    const selectors = [...CLUTTER_SELECTORS, ...(options.selectors ?? [])];
    const keywords = (options.keywords ?? [])
      .map((keyword) => String(keyword).toLowerCase().trim())
      .filter((keyword) => keyword.length >= 3);
    const mentionsTask = (element) => {
      const text = String(element.textContent ?? "").toLowerCase();
      return keywords.some((keyword) => text.includes(keyword));
    };
    const reading = root.querySelector?.("main, article, [role='main']") ?? null;
    let removed = 0;
    for (const element of new Set(selectors.flatMap((selector) => {
      try { return [...root.querySelectorAll(selector)]; } catch (_error) { return []; }
    }))) {
      if (shouldExcludeElement(element)) continue;
      if (element.dataset?.[REMOVED_ATTRIBUTE] === "true") continue;
      if (reading && (element === reading || element.contains?.(reading))) continue;
      if (mentionsTask(element)) continue;
      const height = element.getBoundingClientRect?.().height ?? 0;
      element.dataset.anchorOriginalHtml = element.innerHTML ?? "";
      element.dataset.anchorOriginalHeight = element.style.height ?? "";
      element.dataset[REMOVED_ATTRIBUTE] = "true";
      element.innerHTML = "";
      if (height > 0) element.style.height = `${Math.round(height)}px`;
      removed += 1;
    }
    return removed;
  }

  function restoreRemovedElements(root) {
    let restored = 0;
    for (const element of root.querySelectorAll("[data-anchor-removed='true']")) {
      element.innerHTML = element.dataset.anchorOriginalHtml ?? "";
      element.style.height = element.dataset.anchorOriginalHeight ?? "";
      delete element.dataset.anchorOriginalHtml;
      delete element.dataset.anchorOriginalHeight;
      delete element.dataset[REMOVED_ATTRIBUTE];
      restored += 1;
    }
    return restored;
  }

  /**
   * Replaces a picture's bytes with a low-resolution version of itself instead of only filtering it
   * in CSS, which survives repaints and sites that reset styles. The element keeps its box size, so
   * the layout does not move. Returns false for cross-origin or not-yet-decoded images, leaving the
   * caller to fall back to the CSS filter.
   */
  function downscaleImageSource(image, options = {}) {
    const factor = Math.max(2, Math.round(options.factor ?? DEFAULT_PIXEL_FACTOR));
    const createCanvas = options.createCanvas
      ?? (() => (typeof document === "undefined" ? null : document.createElement("canvas")));
    const width = image.naturalWidth || image.width;
    const height = image.naturalHeight || image.height;
    const canvas = width > 0 && height > 0 ? createCanvas() : null;
    const context = canvas?.getContext?.("2d");
    if (!context) return false;
    canvas.width = Math.max(1, Math.round(width / factor));
    canvas.height = Math.max(1, Math.round(height / factor));
    context.imageSmoothingEnabled = false;
    let source = "";
    try {
      context.drawImage(image, 0, 0, canvas.width, canvas.height);
      source = canvas.toDataURL("image/jpeg", 0.5);
    } catch (_error) {
      return false;
    }
    if (!source) return false;
    const rectangle = image.getBoundingClientRect?.() ?? { width: 0, height: 0 };
    image.dataset.anchorOriginalSrc = image.getAttribute?.("src") ?? image.src ?? "";
    image.dataset.anchorOriginalImageRendering = image.style.imageRendering ?? "";
    image.dataset.anchorPixelated = "true";
    if (rectangle.width > 0 && !image.style.width) image.style.width = `${Math.round(rectangle.width)}px`;
    if (rectangle.height > 0 && !image.style.height) image.style.height = `${Math.round(rectangle.height)}px`;
    image.style.imageRendering = "pixelated";
    if (image.setAttribute) image.setAttribute("src", source); else image.src = source;
    return true;
  }

  function restorePixelatedImages(root) {
    let restored = 0;
    for (const image of root.querySelectorAll("[data-anchor-pixelated='true']")) {
      const original = image.dataset.anchorOriginalSrc ?? "";
      if (image.setAttribute) image.setAttribute("src", original); else image.src = original;
      image.style.imageRendering = image.dataset.anchorOriginalImageRendering ?? "";
      delete image.dataset.anchorOriginalSrc;
      delete image.dataset.anchorOriginalImageRendering;
      delete image.dataset.anchorPixelated;
      restored += 1;
    }
    return restored;
  }

  function splitSentences(text) {
    return (String(text ?? "").match(SENTENCE_PATTERN) ?? [])
      .map((sentence) => sentence.trim())
      .filter(Boolean);
  }

  /** Keeps the leading clauses of an overlong sentence and marks the cut with an ellipsis. */
  function shortenSentence(sentence, maxWords = DEFAULT_MAX_WORDS) {
    const words = sentence.split(/\s+/);
    if (words.length <= maxWords) return sentence;
    const clauses = sentence.split(/(?<=[,;:—])\s+/);
    let kept = clauses[0];
    for (let index = 1; index < clauses.length && kept.split(/\s+/).length < Math.min(maxWords, 12); index++) {
      kept = `${kept} ${clauses[index]}`;
    }
    return `${kept.split(/\s+/).slice(0, maxWords).join(" ").replace(/[,;:—-]$/, "")}…`;
  }

  /**
   * Rewrites the paragraph text itself: sentences that never mention the task are deleted and
   * overlong ones are cut down to their leading clauses. The paragraph's measured height is pinned
   * as `min-height` so shortening text never reflows the page, and the original markup is kept in a
   * data attribute for `restoreRewrittenText`.
   */
  function rewriteSentences(root, options = {}) {
    const keywords = (options.keywords ?? [])
      .map((keyword) => String(keyword).toLowerCase().trim())
      .filter((keyword) => keyword.length >= 3);
    const maxWords = Math.max(8, options.maxWords ?? DEFAULT_MAX_WORDS);
    let changed = 0;
    for (const element of paragraphCandidates(root)) {
      if (element.dataset?.anchorRewritten === "true") continue;
      const original = String(element.textContent ?? "").trim();
      const sentences = splitSentences(original);
      if (sentences.length === 0) continue;
      const kept = sentences
        .filter((sentence) => keywords.length === 0
          || keywords.some((keyword) => sentence.toLowerCase().includes(keyword)))
        .map((sentence) => shortenSentence(sentence, maxWords));
      const next = kept.join(" ").trim();
      if (next === original) continue;
      const height = element.getBoundingClientRect?.().height ?? 0;
      element.dataset.anchorOriginalHtml = element.innerHTML ?? original;
      element.dataset.anchorOriginalMinHeight = element.style.minHeight ?? "";
      element.dataset.anchorRewritten = "true";
      if (height > 0) element.style.minHeight = `${Math.round(height)}px`;
      element.innerHTML = "";
      element.textContent = next;
      changed += 1;
    }
    return changed;
  }

  function restoreRewrittenText(root) {
    let restored = 0;
    for (const element of root.querySelectorAll("[data-anchor-rewritten='true']")) {
      element.innerHTML = element.dataset.anchorOriginalHtml ?? "";
      element.style.minHeight = element.dataset.anchorOriginalMinHeight ?? "";
      delete element.dataset.anchorOriginalHtml;
      delete element.dataset.anchorOriginalMinHeight;
      delete element.dataset.anchorRewritten;
      restored += 1;
    }
    return restored;
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
    restoreRemovedElements(root);
    restoreRewrittenText(root);
    restorePixelatedImages(root);
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

  return {
    classifyImage,
    isOffTaskImage,
    isProtectedPage,
    shouldExcludeElement,
    applyImageFiltering,
    clearImageFilters,
    maskFutureText,
    clearFutureTextMasks,
    removeOffTaskElements,
    restoreRemovedElements,
    rewriteSentences,
    restoreRewrittenText,
    shortenSentence,
    downscaleImageSource,
    restorePixelatedImages,
    applyAnimationSuppression,
    clearInterventions,
    createReadingTracker,
    injectStyles,
  };
});
