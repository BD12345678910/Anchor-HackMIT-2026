(function initializeAnchorAdapterState(globalScope, factory) {
  const api = factory();
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  globalScope.AnchorAdapterState = api;
})(typeof globalThis !== "undefined" ? globalThis : this, function createAnchorAdapterState() {
  function normalizeOrigin(url) {
    try {
      const parsed = new URL(url);
      return ["http:", "https:"].includes(parsed.protocol) ? parsed.origin : null;
    } catch (_error) {
      return null;
    }
  }

  function setOriginEnabled(origins, origin, enabled) {
    const normalized = normalizeOrigin(origin);
    const next = new Set((origins ?? []).map(normalizeOrigin).filter(Boolean));
    if (!normalized) return [...next].sort();
    if (enabled) next.add(normalized); else next.delete(normalized);
    return [...next].sort();
  }

  function isOriginEnabled(origins, url, deniedOrigins = []) {
    const origin = normalizeOrigin(url);
    if (!origin) return false;
    const denied = new Set((deniedOrigins ?? []).map(normalizeOrigin).filter(Boolean));
    return !denied.has(origin) && (origins ?? []).map(normalizeOrigin).includes(origin);
  }

  function messagesForToolkitState(state = {}, saved = {}) {
    return [
      { command: "setVisualFilter", enabled: state.imageBlur ?? saved.imageBlur ?? true, threshold: state.threshold ?? saved.threshold ?? 0.62 },
      { command: "setFutureTextMask", enabled: state.futureTextMask ?? saved.futureTextMask ?? false, lookahead: state.lookahead ?? saved.lookahead ?? 1 },
      { command: "setAnimationSuppression", enabled: state.suppressAnimations ?? saved.suppressAnimations ?? false },
    ];
  }

  return { normalizeOrigin, setOriginEnabled, isOriginEnabled, messagesForToolkitState };
});
