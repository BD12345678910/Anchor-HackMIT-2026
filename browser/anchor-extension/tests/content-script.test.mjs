import assert from "node:assert/strict";
import { createRequire } from "node:module";
import test from "node:test";

const require = createRequire(import.meta.url);
const {
  classifyImage,
  isProtectedPage,
  shouldExcludeElement,
  applyImageFiltering,
  maskFutureText,
  clearInterventions,
  createReadingTracker,
} = require("../content-script.js");

class FakeClassList {
  constructor() { this.values = new Set(); }
  add(...items) { items.forEach((item) => this.values.add(item)); }
  remove(...items) { items.forEach((item) => this.values.delete(item)); }
  contains(item) { return this.values.has(item); }
}

class FakeElement {
  constructor(tagName, options = {}) {
    this.tagName = tagName.toUpperCase();
    this.type = options.type ?? "";
    this.isContentEditable = Boolean(options.isContentEditable);
    this.width = options.width ?? 0;
    this.height = options.height ?? 0;
    this.naturalWidth = options.naturalWidth ?? this.width;
    this.naturalHeight = options.naturalHeight ?? this.height;
    this.complete = true;
    this.dataset = {};
    this.style = { filter: options.filter ?? "", opacity: "", transition: "" };
    this.classList = new FakeClassList();
    this.textContent = options.text ?? "";
    this.parentElement = options.parentElement ?? null;
    this._rect = options.rect ?? { top: 0, bottom: this.height, width: this.width, height: this.height };
  }
  getBoundingClientRect() { return this._rect; }
  closest(selector) {
    if (selector.includes("form") && this.tagName === "FORM") return this;
    if (selector.includes("input") && this.tagName === "INPUT") return this;
    if (selector.includes("video") && this.tagName === "VIDEO") return this;
    if (selector.includes("[contenteditable") && this.isContentEditable) return this;
    return this.parentElement?.closest?.(selector) ?? null;
  }
}

class FakeRoot {
  constructor(images = [], paragraphs = []) { this.images = images; this.paragraphs = paragraphs; }
  querySelectorAll(selector) {
    if (selector === "img") return this.images;
    if (selector.includes("p")) return this.paragraphs;
    if (selector.includes("anchor-")) {
      return [...this.images, ...this.paragraphs].filter((element) =>
        (selector.includes(".anchor-image-filtered") && element.classList.contains("anchor-image-filtered"))
        || (selector.includes(".anchor-future-mask") && element.classList.contains("anchor-future-mask"))
        || (selector.includes(".anchor-recovery-anchor") && element.classList.contains("anchor-recovery-anchor"))
        || (selector.includes("data-anchor-filtered") && element.dataset.anchorFiltered === "true"));
    }
    return [];
  }
}

test("image scoring favors large animated irrelevant images and remains bounded", () => {
  const quiet = classifyImage({ width: 80, height: 60, viewportWidth: 1200, viewportHeight: 800, animated: false, contrast: 0.2, relevance: 0.9, top: 700 });
  const distracting = classifyImage({ width: 900, height: 620, viewportWidth: 1200, viewportHeight: 800, animated: true, contrast: 0.9, relevance: 0.05, top: 40 });
  assert.ok(quiet >= 0 && quiet <= 1);
  assert.ok(distracting >= 0 && distracting <= 1);
  assert.ok(distracting > quiet);
  assert.ok(distracting > 0.65);
});

test("protected origins and sensitive form or media elements are excluded", () => {
  assert.equal(isProtectedPage("chrome://settings", { hasPassword: false, hasPayment: false }), true);
  assert.equal(isProtectedPage("https://bank.test/pay", { hasPassword: false, hasPayment: true }), true);
  assert.equal(isProtectedPage("https://docs.test/read", { hasPassword: false, hasPayment: false }), false);
  assert.equal(shouldExcludeElement(new FakeElement("input", { type: "password" })), true);
  assert.equal(shouldExcludeElement(new FakeElement("video")), true);
  assert.equal(shouldExcludeElement(new FakeElement("div", { isContentEditable: true })), true);
  assert.equal(shouldExcludeElement(new FakeElement("img")), false);
});

test("dynamic filtering blurs only images over threshold and preserves original style", () => {
  const small = new FakeElement("img", { width: 90, height: 60, filter: "sepia(1)" });
  const hero = new FakeElement("img", { width: 900, height: 620, rect: { top: 20, bottom: 640, width: 900, height: 620 } });
  const root = new FakeRoot([small, hero]);
  const changed = applyImageFiltering(root, { viewportWidth: 1200, viewportHeight: 800, threshold: 0.55, relevance: () => 0.05 });
  assert.equal(changed, 1);
  assert.equal(small.style.filter, "sepia(1)");
  assert.match(hero.style.filter, /blur/);
  assert.equal(hero.dataset.anchorFiltered, "true");
});

test("future text mask excludes current and past paragraphs", () => {
  const paragraphs = Array.from({ length: 6 }, (_, index) => new FakeElement("p", { text: `Paragraph ${index}` }));
  const root = new FakeRoot([], paragraphs);
  const masked = maskFutureText(root, 2, { lookahead: 1 });
  assert.equal(masked, 2);
  assert.equal(paragraphs[3].classList.contains("anchor-future-mask"), false);
  assert.equal(paragraphs[4].classList.contains("anchor-future-mask"), true);
  assert.equal(paragraphs[5].classList.contains("anchor-future-mask"), true);
});

test("cleanup restores filters and is idempotent", () => {
  const image = new FakeElement("img", { width: 900, height: 620, filter: "sepia(1)", rect: { top: 0, bottom: 620, width: 900, height: 620 } });
  const paragraph = new FakeElement("p");
  const root = new FakeRoot([image], [paragraph]);
  applyImageFiltering(root, { viewportWidth: 1200, viewportHeight: 800, threshold: 0.2, relevance: () => 0 });
  maskFutureText(root, -1, { lookahead: 0 });
  clearInterventions(root);
  paragraph.classList.add("anchor-recovery-anchor");
  clearInterventions(root);
  assert.equal(image.style.filter, "sepia(1)");
  assert.equal(paragraph.classList.contains("anchor-future-mask"), false);
  assert.equal(paragraph.classList.contains("anchor-recovery-anchor"), false);
});

test("reading tracker detects a large skip and repeated phrase dwell", () => {
  const events = [];
  const tracker = createReadingTracker((event) => events.push(event), { skipThreshold: 0.3, dwellThreshold: 3 });
  tracker.observeProgress(0.1, 1000);
  tracker.observeProgress(0.55, 1500);
  tracker.observePhrase("hard phrase", 2000);
  tracker.observePhrase("hard phrase", 3000);
  tracker.observePhrase("hard phrase", 4000);
  assert.equal(events[0].type, "reading-skip");
  assert.equal(events[1].type, "stuck-phrase");
  assert.equal(events[1].phrase, "hard phrase");
});
