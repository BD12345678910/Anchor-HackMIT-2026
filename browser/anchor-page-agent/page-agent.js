/**
 * Drives the page edits from the desktop app: nothing is installed in the browser.
 *
 * Anchor injects `focus-engine.js` and then this file through the DevTools protocol, and calls
 * `AnchorPageAgent.apply(state)` whenever the toolkit state changes. Everything it does is
 * reversible through `AnchorPageAgent.clear()`, and a debounced MutationObserver re-applies the
 * edits to content the page loads later.
 */
(function initializeAnchorPageAgent(globalScope, factory) {
  const api = factory(globalScope);
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  globalScope.AnchorPageAgent = api;
})(typeof globalThis !== "undefined" ? globalThis : this, function createAnchorPageAgent(globalScope) {
  const REAPPLY_DELAY_MS = 120;
  const DEFAULT_STATE = {
    imageBlur: false,
    threshold: 0.5,
    clutterRemoval: false,
    simplifyText: false,
    maxWords: 28,
    suppressAnimations: false,
    keywords: [],
  };

  let state = { ...DEFAULT_STATE };
  let observer = null;
  let timer = null;
  let lastCounts = { images: 0, removed: 0, rewritten: 0 };

  function engine() {
    const value = globalScope.AnchorFocusEngine;
    if (!value) throw new Error("The Anchor page engine is not loaded.");
    return value;
  }

  function documentOf(root) {
    return root ?? globalScope.document;
  }

  /** Pages with a password or payment field are never touched. */
  function isProtected(root) {
    const page = documentOf(root);
    if (!page?.querySelector) return true;
    return engine().isProtectedPage(globalScope.location?.href ?? "", {
      hasPassword: Boolean(page.querySelector("input[type='password']")),
      hasPayment: Boolean(page.querySelector("[autocomplete^='cc-'], input[name*='card' i]")),
    });
  }

  function edit(root) {
    const page = documentOf(root);
    const focus = engine();
    const counts = { images: 0, removed: 0, rewritten: 0 };
    if (state.imageBlur) {
      counts.images = focus.applyImageFiltering(page, { threshold: state.threshold, rewriteSource: true });
    }
    if (state.clutterRemoval) {
      counts.removed = focus.removeOffTaskElements(page, { keywords: state.keywords });
    }
    if (state.simplifyText) {
      counts.rewritten = focus.rewriteSentences(page, { keywords: state.keywords, maxWords: state.maxWords });
    }
    focus.applyAnimationSuppression(page, state.suppressAnimations);
    lastCounts = {
      images: lastCounts.images + counts.images,
      removed: lastCounts.removed + counts.removed,
      rewritten: lastCounts.rewritten + counts.rewritten,
    };
    return counts;
  }

  function watch(root) {
    const page = documentOf(root);
    if (observer || !page?.documentElement || typeof globalScope.MutationObserver !== "function") return;
    observer = new globalScope.MutationObserver(() => {
      globalScope.clearTimeout?.(timer);
      timer = globalScope.setTimeout(() => edit(page), REAPPLY_DELAY_MS);
    });
    observer.observe(page.documentElement, { childList: true, subtree: true });
  }

  function unwatch() {
    globalScope.clearTimeout?.(timer);
    timer = null;
    observer?.disconnect?.();
    observer = null;
  }

  function apply(next = {}, root) {
    const page = documentOf(root);
    if (isProtected(page)) return { applied: false, reason: "protected-page" };
    const wanted = { ...DEFAULT_STATE, ...next };
    engine().injectStyles?.();
    const previous = state;
    state = wanted;
    const focus = engine();
    if (previous.imageBlur && !wanted.imageBlur) {
      focus.clearImageFilters(page);
      focus.restorePixelatedImages(page);
    }
    if (previous.clutterRemoval && !wanted.clutterRemoval) focus.restoreRemovedElements(page);
    if (previous.simplifyText && !wanted.simplifyText) focus.restoreRewrittenText(page);
    const counts = edit(page);
    if (wanted.imageBlur || wanted.clutterRemoval || wanted.simplifyText) watch(page); else unwatch();
    return { applied: true, ...counts };
  }

  function clear(root) {
    const page = documentOf(root);
    unwatch();
    state = { ...DEFAULT_STATE };
    lastCounts = { images: 0, removed: 0, rewritten: 0 };
    engine().clearInterventions(page);
    return { applied: true };
  }

  function status() {
    return { ...lastCounts, watching: observer !== null, state: { ...state } };
  }

  return { apply, clear, status, isProtected };
});
