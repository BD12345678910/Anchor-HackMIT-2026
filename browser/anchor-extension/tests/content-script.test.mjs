import assert from "node:assert/strict";
import { createRequire } from "node:module";
import test from "node:test";

const require = createRequire(import.meta.url);
const {
  classifyImage,
  isProtectedPage,
  shouldExcludeElement,
  applyImageFiltering,
  clearImageFilters,
  maskFutureText,
  clearInterventions,
  clearFutureTextMasks,
  applyAnimationSuppression,
  createReadingTracker,
  removeOffTaskElements,
  restoreRemovedElements,
  rewriteSentences,
  restoreRewrittenText,
  shortenSentence,
  downscaleImageSource,
  restorePixelatedImages,
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
    this.style = { filter: options.filter ?? "", opacity: "", transition: "", backgroundImage: options.backgroundImage ?? "", animationPlayState: options.animationPlayState ?? "" };
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
  constructor(images = [], paragraphs = [], backgrounds = [], animated = []) { this.images = images; this.paragraphs = paragraphs; this.backgrounds = backgrounds; this.animated = animated; }
  querySelectorAll(selector) {
    if (selector === "img") return this.images;
    if (selector.includes("background-image")) return this.backgrounds;
    if (selector.includes("data-anchor-animated")) return this.animated;
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

test("CSS background images can be filtered and animation suppression is reversible", () => {
  const background = new FakeElement("div", { width: 900, height: 620, backgroundImage: "url(hero.jpg)", rect: { top: 20, bottom: 640, width: 900, height: 620 } });
  const animated = new FakeElement("div", { animationPlayState: "running" });
  animated.dataset.anchorAnimated = "true";
  const root = new FakeRoot([], [], [background], [animated]);

  assert.equal(applyImageFiltering(root, { viewportWidth: 1200, viewportHeight: 800, threshold: 0.5, relevance: () => 0 }), 1);
  assert.equal(applyAnimationSuppression(root, true), 1);
  assert.match(background.style.filter, /blur/);
  assert.equal(animated.style.animationPlayState, "paused");
  applyAnimationSuppression(root, false);
  assert.equal(animated.style.animationPlayState, "running");
});

test("clearing future text masks preserves unrelated image blur", () => {
  const image = new FakeElement("img", { width: 900, height: 620, rect: { top: 0, bottom: 620, width: 900, height: 620 } });
  const paragraph = new FakeElement("p");
  const root = new FakeRoot([image], [paragraph]);
  applyImageFiltering(root, { viewportWidth: 1200, viewportHeight: 800, threshold: 0.2, relevance: () => 0 });
  maskFutureText(root, -1, { lookahead: 0 });

  clearFutureTextMasks(root);

  assert.equal(paragraph.classList.contains("anchor-future-mask"), false);
  assert.equal(image.dataset.anchorFiltered, "true");
  assert.match(image.style.filter, /blur/);
});

test("clearing image filters preserves future text masks", () => {
  const image = new FakeElement("img", { width: 900, height: 620, filter: "sepia(1)", rect: { top: 0, bottom: 620, width: 900, height: 620 } });
  const paragraph = new FakeElement("p");
  const root = new FakeRoot([image], [paragraph]);
  applyImageFiltering(root, { viewportWidth: 1200, viewportHeight: 800, threshold: 0.2, relevance: () => 0 });
  maskFutureText(root, -1, { lookahead: 0 });

  clearImageFilters(root);

  assert.equal(image.style.filter, "sepia(1)");
  assert.equal(paragraph.classList.contains("anchor-future-mask"), true);
});

class FakeBlock {
  constructor(options = {}) {
    this.tagName = (options.tagName ?? "div").toUpperCase();
    this.id = options.id ?? "";
    this.dataset = {};
    this.style = { height: "", minHeight: "", width: "", imageRendering: "" };
    this.classList = new FakeClassList();
    this.innerHTML = options.html ?? "";
    this.textContent = options.text ?? "";
    this.isContentEditable = false;
    this._height = options.height ?? 0;
  }
  getBoundingClientRect() { return { top: 0, bottom: this._height, width: 600, height: this._height }; }
  closest() { return null; }
  contains() { return false; }
}

class PageRoot {
  constructor(blocks = [], paragraphs = []) { this.blocks = blocks; this.paragraphs = paragraphs; }
  querySelector() { return null; }
  querySelectorAll(selector) {
    const all = [...this.blocks, ...this.paragraphs];
    if (selector.startsWith("[data-anchor-")) {
      const attribute = selector.slice(13, selector.indexOf("=")).replace(/-(.)/g, (_match, letter) => letter.toUpperCase());
      return all.filter((element) => element.dataset[`anchor${attribute[0].toUpperCase()}${attribute.slice(1)}`] === "true");
    }
    if (selector === "p, article li, main li") return this.paragraphs;
    return this.blocks.filter((block) => selector === block.tagName.toLowerCase() || selector === `#${block.id}`);
  }
}

test("off-task page furniture is emptied out of the HTML but keeps its height", () => {
  const advert = new FakeBlock({ tagName: "aside", html: "<p>Buy now</p>", text: "Buy now", height: 240 });
  const related = new FakeBlock({ tagName: "aside", html: "<p>More about cats</p>", text: "More about cats", height: 180 });
  const root = new PageRoot([advert, related]);

  assert.equal(removeOffTaskElements(root, { keywords: ["cats"] }), 1);
  assert.equal(advert.innerHTML, "");
  assert.equal(advert.style.height, "240px");
  assert.equal(related.innerHTML, "<p>More about cats</p>");

  assert.equal(restoreRemovedElements(root), 1);
  assert.equal(advert.innerHTML, "<p>Buy now</p>");
  assert.equal(advert.style.height, "");
});

test("irrelevant sentences are deleted, long ones shortened, and the paragraph keeps its box", () => {
  const paragraph = new FakeBlock({
    tagName: "p",
    html: "<span>original</span>",
    height: 96,
    text: "Cats groom themselves. Subscribe to our newsletter for deals. "
      + "Cats also sleep for roughly sixteen hours each day, which is far more than most other "
      + "domesticated animals and is one reason they seem so calm to their owners at home.",
  });
  const root = new PageRoot([], [paragraph]);

  assert.equal(rewriteSentences(root, { keywords: ["cats"], maxWords: 12 }), 1);
  assert.equal(paragraph.textContent.includes("newsletter"), false);
  assert.equal(paragraph.textContent.startsWith("Cats groom themselves."), true);
  assert.match(paragraph.textContent, /…$/);
  assert.equal(paragraph.style.minHeight, "96px");

  assert.equal(restoreRewrittenText(root), 1);
  assert.equal(paragraph.innerHTML, "<span>original</span>");
  assert.equal(paragraph.style.minHeight, "");
});

test("shortening keeps the leading clauses of a sentence", () => {
  const sentence = "Dynamic programming, which trades memory for time, solves overlapping subproblems by "
    + "storing each answer once and reusing it later.";
  assert.equal(shortenSentence(sentence, 40), sentence);
  const short = shortenSentence(sentence, 10);
  assert.equal(short.startsWith("Dynamic programming, which trades memory for time,"), true);
  assert.ok(short.split(/\s+/).length <= 11);
});

test("pictures are rewritten to a low-resolution source and restored", () => {
  const canvas = {
    width: 0,
    height: 0,
    getContext: () => ({ imageSmoothingEnabled: true, drawImage() { } }),
    toDataURL: () => "data:image/jpeg;base64,tiny",
  };
  const image = new FakeBlock({ tagName: "img" });
  image.naturalWidth = 600;
  image.naturalHeight = 400;
  image.src = "https://cdn.test/hero.jpg";
  image.getAttribute = (name) => (name === "src" ? image.src : null);
  image.setAttribute = (name, value) => { if (name === "src") image.src = value; };
  const root = new PageRoot([image]);

  assert.equal(downscaleImageSource(image, { factor: 10, createCanvas: () => canvas }), true);
  assert.equal(image.src, "data:image/jpeg;base64,tiny");
  assert.equal(canvas.width, 60);
  assert.equal(image.style.imageRendering, "pixelated");

  assert.equal(restorePixelatedImages(root), 1);
  assert.equal(image.src, "https://cdn.test/hero.jpg");
  assert.equal(image.style.imageRendering, "");
});

test("a cross-origin canvas leaves the picture untouched so CSS blur can take over", () => {
  const tainted = {
    width: 0,
    height: 0,
    getContext: () => ({ imageSmoothingEnabled: true, drawImage() { } }),
    toDataURL: () => { throw new Error("tainted canvas"); },
  };
  const image = new FakeBlock({ tagName: "img" });
  image.naturalWidth = 600;
  image.naturalHeight = 400;
  image.src = "https://cdn.test/hero.jpg";

  assert.equal(downscaleImageSource(image, { createCanvas: () => tainted }), false);
  assert.equal(image.src, "https://cdn.test/hero.jpg");
  assert.equal(image.dataset.anchorPixelated, undefined);
});
