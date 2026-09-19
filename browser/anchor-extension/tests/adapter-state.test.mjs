import assert from "node:assert/strict";
import { createRequire } from "node:module";
import test from "node:test";

const require = createRequire(import.meta.url);
const { normalizeOrigin, setOriginEnabled, isOriginEnabled, messagesForToolkitState } = require("../adapter-state.js");

test("only explicit HTTP origins are enabled and a denied origin wins", () => {
  const enabled = setOriginEnabled([], "https://USACO.guide/problems/?x=1", true);
  assert.deepEqual(enabled, ["https://usaco.guide"]);
  assert.equal(isOriginEnabled(enabled, "https://usaco.guide/dashboard", []), true);
  assert.equal(isOriginEnabled(enabled, "https://usaco.guide/dashboard", ["https://usaco.guide"]), false);
  assert.equal(normalizeOrigin("chrome://settings"), null);
});

test("disabling an origin survives paths and duplicate entries", () => {
  const enabled = ["https://codeforces.com", "https://codeforces.com"];
  assert.deepEqual(setOriginEnabled(enabled, "https://codeforces.com/problemset", false), []);
});

test("desktop toolkit state becomes independent page commands", () => {
  const messages = messagesForToolkitState(
    { imageBlur: false, futureTextMask: true, suppressAnimations: true, lookahead: 2 },
    { threshold: 0.7 },
  );
  assert.deepEqual(messages, [
    { command: "setVisualFilter", enabled: false, threshold: 0.7 },
    { command: "setFutureTextMask", enabled: true, lookahead: 2 },
    { command: "setAnimationSuppression", enabled: true },
  ]);
});
