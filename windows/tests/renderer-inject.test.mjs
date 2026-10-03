import assert from "node:assert/strict";
import fs from "node:fs/promises";
import path from "node:path";
import vm from "node:vm";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const windowsRoot = path.resolve(here, "..");
const template = await fs.readFile(path.join(windowsRoot, "assets", "renderer-inject.js"), "utf8");
const css = await fs.readFile(path.join(windowsRoot, "assets", "dream-skin.css"), "utf8");
const buildPayload = (config = {}, fullscreenArtDataUrl = null) => template
  .replace("__DREAM_CSS_JSON__", JSON.stringify(".fixture { color: blue; }"))
  .replace("__DREAM_ART_JSON__", JSON.stringify("data:image/png;base64,AA=="))
  .replace("__DREAM_FULLSCREEN_ART_JSON__", JSON.stringify(fullscreenArtDataUrl))
  .replace("__DREAM_THEME_JSON__", JSON.stringify(config));
const payload = buildPayload();

assert.doesNotMatch(
  css,
  /main\.main-surface\s*>\s*header\.app-header-tint\s*\{[^}]*\b(?:position|z-index)\s*:/,
  "The skin must preserve Codex's native fixed header so the side-panel toggle remains reachable.",
);
assert.doesNotMatch(
  css,
  /(?<!\.dream-home-legacy)\.dream-home\s*>/,
  "Deep home geometry must not apply to a new Codex home DOM.",
);
assert.match(
  css,
  /\.dream-home\.dream-home-legacy\s*>/,
  "Legacy home geometry must remain available behind an explicit compatibility class.",
);

function createFixture({
  shellPresent,
  staleSkin = false,
  homePresent = false,
  utilityPresent = false,
  shellAppearance = "dark",
  computedColorScheme = "",
  osAppearance = "light",
  analysisFixture = null,
  composerButtonLabel = null,
  sidebarPresent = true,
  homeContentsLayout = false,
  composerPresent = true,
  dotsPresent = false,
}) {
  const nodes = new Map();
  const rootClasses = new Set(staleSkin ? ["codex-dream-skin"] : []);
  const rootStyles = new Map(staleSkin ? [["--dream-art", "url(\"blob:stale\")"]] : []);
  const revokedUrls = [];
  const observers = [];
  const timeouts = [];
  let objectUrlCount = 0;
  let hasShell = shellPresent;
  let hasSidebar = sidebarPresent;
  let activeShellIndex = 0;
  let secondShellPresent = false;
  let root;

  const queueRootClassMutation = () => {
    for (const observer of observers) {
      if (observer.target !== root || !observer.options?.attributes) continue;
      if (observer.options.attributeFilter && !observer.options.attributeFilter.includes("class")) continue;
      observer.records.push({ type: "attributes", attributeName: "class", target: root });
    }
  };
  const makeClassList = (classes = new Set(), onMutation = () => {}) => ({
    add(...values) {
      let changed = false;
      for (const value of values) {
        if (!classes.has(value)) { classes.add(value); changed = true; }
      }
      if (changed) onMutation();
    },
    remove(...values) {
      let changed = false;
      for (const value of values) changed = classes.delete(value) || changed;
      if (changed) onMutation();
    },
    toggle(value, enabled) {
      const changed = enabled ? !classes.has(value) : classes.has(value);
      if (enabled) classes.add(value);
      else classes.delete(value);
      if (changed) onMutation();
    },
    contains(value) { return classes.has(value); },
  });

  root = {
    className: shellAppearance,
    classList: makeClassList(rootClasses, queueRootClassMutation),
    getAttribute() { return null; },
    style: {
      setProperty(key, value) { rootStyles.set(key, value); },
      removeProperty(key) { rootStyles.delete(key); },
    },
    appendChild(node) {
      node.parentElement = root;
      nodes.set(node.id, node);
    },
  };
  const body = {
    className: "",
    getAttribute() { return null; },
    appendChild(node) {
      node.parentElement = body;
      nodes.set(node.id, node);
    },
    insertBefore(node) {
      node.parentElement = body;
      nodes.set(node.id, node);
    },
  };
  const shellMain = {
    classList: makeClassList(),
    contains() { return false; },
    getBoundingClientRect() {
      if (activeShellIndex !== 0) return { left: 0, top: 0, width: 0, height: 0 };
      return { left: hasSidebar ? 290 : 0, top: 36, width: hasSidebar ? 990 : 1280, height: 764 };
    },
  };
  const secondShell = {
    classList: makeClassList(),
    contains() { return false; },
    getBoundingClientRect() {
      return activeShellIndex === 1
        ? { left: 290, top: 36, width: 990, height: 764 }
        : { left: 0, top: 0, width: 0, height: 0 };
    },
  };
  const shellSidebar = {
    classList: makeClassList(),
    getBoundingClientRect() {
      return { left: 0, top: 36, right: 290, bottom: 800, width: 290, height: 764 };
    },
  };
  const routeClasses = new Set();
  const utilityClasses = new Set();
  const utilityNode = { classList: makeClassList(utilityClasses) };
  const composerButtonClasses = new Set(["bg-token-foreground"]);
  const composerButton = {
    classList: makeClassList(composerButtonClasses),
    getAttribute(name) {
      if (name === "aria-label") return composerButtonLabel;
      if (name === "type") return "button";
      return null;
    },
  };
  const composer = {
    classList: makeClassList(),
    getBoundingClientRect() {
      return { left: 500, top: 680, right: 1180, bottom: 760, width: 680, height: 80 };
    },
    querySelectorAll(selector) {
      if (selector.includes('button[class~="bg-token-foreground"]') && composerButtonLabel) return [composerButton];
      return [];
    },
  };
  const routeMain = {
    classList: makeClassList(routeClasses),
    querySelectorAll(selector) {
      if (selector === '[class*="_homeUtilityBar_"]' && utilityPresent) return [utilityNode];
      return [];
    },
  };
  if (homeContentsLayout) routeMain.firstElementChild = { firstElementChild: {
    fixtureDisplay: "contents",
    firstElementChild: { classList: makeClassList(new Set(["group/home-takeover"])), querySelector: () => ({}) },
  } };
  const staleHome = { classList: makeClassList(new Set(["dream-home"])) };
  const staleShell = { classList: makeClassList(new Set(["dream-home-shell"])) };

  const createElement = (tagName) => {
    if (tagName === "canvas" && analysisFixture) {
      return {
        width: 0,
        height: 0,
        getContext() {
          return {
            drawImage() {},
            getImageData() { return { data: analysisFixture.pixels }; },
          };
        },
      };
    }
    return {
      id: "",
      tagName: String(tagName || "div").toUpperCase(),
      dataset: {},
      style: {},
      listeners: {},
      addEventListener(name, callback) { this.listeners[name] = callback; },
      play() { return Promise.resolve(); },
      getContext() { return { drawImage() {} }; },
      classList: makeClassList(),
      parentElement: null,
      textContent: "",
      innerHTML: "",
      setAttribute() {},
      remove() { nodes.delete(this.id); },
    };
  };
  if (staleSkin) {
    const style = createElement();
    style.id = "codex-dream-skin-style";
    nodes.set(style.id, style);
    const chrome = createElement();
    chrome.id = "codex-dream-skin-chrome";
    nodes.set(chrome.id, chrome);
  }

  const document = {
    documentElement: root,
    head: root,
    body,
    createElement,
    getElementById(id) { return nodes.get(id) ?? null; },
    querySelector(selector) {
      if (selector.includes("data-codex-pet-id")) return null;
      if (selector.includes("data-app-shell-main-surface") || selector === "main.main-surface") {
        return hasShell ? shellMain : null;
      }
      if (selector.includes("app-shell-left-panel") || selector.includes("floating-left-panel") ||
          selector.includes("left-panel-appearance")) {
        return hasShell && hasSidebar ? shellSidebar : null;
      }
      if (selector.includes("composer-surface-chrome") || selector.includes("data-codex-composer")) {
        return hasShell && composerPresent ? composer : null;
      }
      if (selector.includes('.messaging-root.messaging-embedded')) return hasShell && dotsPresent ? routeMain : null;
      if (selector.includes("home-icon") || selector.includes("data-home-ambient-suggestions")) {
        return hasShell && homePresent ? routeMain : null;
      }
      return null;
    },
    querySelectorAll(selector) {
      if (selector.includes('data-app-shell-main-surface')) {
        return hasShell ? [shellMain, ...(secondShellPresent ? [secondShell] : [])] : [];
      }
      if (selector.includes("composer-surface-chrome") || selector.includes("data-codex-composer")) {
        return hasShell && composerPresent ? [composer] : [];
      }
      if (selector === "aside") return hasShell && hasSidebar ? [shellSidebar] : [];
      if (selector === '[role="main"]') return hasShell && !dotsPresent ? [routeMain] : [];
      if (selector === ".dream-task") return routeClasses.has("dream-task") ? [routeMain] : [];
      if (selector === ".dream-home-utility") {
        return utilityClasses.has("dream-home-utility") ? [utilityNode] : [];
      }
      if (selector === ".dream-send-lightning") {
        return composerButtonClasses.has("dream-send-lightning") ? [composerButton] : [];
      }
      if (selector === ".dream-shell-main") return [shellMain, secondShell].filter(node => node.classList.contains("dream-shell-main"));
      if (selector === ".dream-shell-sidebar") {
        return shellSidebar.classList.contains("dream-shell-sidebar") ? [shellSidebar] : [];
      }
      if (selector === ".dream-composer") return composer.classList.contains("dream-composer") ? [composer] : [];
      if (!staleSkin) return [];
      if (selector === ".dream-home") return [staleHome];
      if (selector === ".dream-home-shell") return [staleShell];
      return [];
    },
  };
  const context = {
    window: {
      matchMedia() { return { matches: osAppearance === "dark" }; },
    },
    document,
    MutationObserver: class {
      constructor(callback) {
        this.callback = callback;
        this.records = [];
        this.target = null;
        this.options = null;
        observers.push(this);
      }
      observe(target, options = {}) {
        this.target = target;
        this.options = options;
      }
      disconnect() {
        this.target = null;
        this.records = [];
      }
      takeRecords() {
        const records = this.records;
        this.records = [];
        return records;
      }
    },
    URL: {
      createObjectURL() { objectUrlCount += 1; return `blob:fixture-${objectUrlCount}`; },
      revokeObjectURL(value) { revokedUrls.push(value); },
    },
    Blob,
    Uint8Array,
    atob,
    setInterval: () => 1,
    clearInterval: () => {},
    setTimeout: (callback, delay) => { timeouts.push({ callback, delay }); return timeouts.length; },
    clearTimeout: () => {},
    getComputedStyle(node) { return { colorScheme: computedColorScheme, display: node?.fixtureDisplay }; },
    innerWidth: 1280,
    innerHeight: 800,
  };
  if (analysisFixture) {
    context.Image = class {
      naturalWidth = analysisFixture.naturalWidth;
      naturalHeight = analysisFixture.naturalHeight;
      set src(_) { this.onload(); }
    };
  }

  return {
    context,
    nodes,
    observers,
    timeouts,
    rootClasses,
    rootStyles,
    revokedUrls,
    routeClasses,
    utilityClasses,
    composerButtonClasses,
    setShellPresent(value) { hasShell = value; },
    setSidebarPresent(value) { hasSidebar = value; },
    shellMain,
    secondShell,
    showCachedShell(index) { secondShellPresent = true; activeShellIndex = index; },
  };
}

const main = createFixture({ shellPresent: true });
const dots = createFixture({ shellPresent: true, composerPresent: false, dotsPresent: true });
vm.runInNewContext(payload, dots.context);
assert.equal(dots.rootClasses.has('codex-dream-skin'), true,
  'Dots can keep the wallpaper without a chat composer or role=main');
assert.equal(dots.shellMain.classList.contains('dream-shell-main'), true);
assert.equal(dots.nodes.has('codex-dream-skin-style'), true);
const modernHome = createFixture({ shellPresent: true, homePresent: true, homeContentsLayout: true });
vm.runInNewContext(payload, modernHome.context);
assert.equal(modernHome.routeClasses.has("dream-home"), true);
assert.equal(modernHome.routeClasses.has("dream-home-legacy"), false,
  "a native contents/takeover home layout must not acquire the old empty wallpaper card");
const mainResult = vm.runInNewContext(payload, main.context);
assert.equal(mainResult.installed, true);
assert.equal(main.rootClasses.has("codex-dream-skin"), true);
assert.equal(main.rootStyles.get("--dream-art"), 'url("blob:fixture-1")');
assert.equal(main.nodes.has("codex-dream-skin-style"), true);
assert.equal(main.nodes.has("codex-dream-skin-chrome"), true);
assert.equal(main.rootClasses.has("dream-theme-dark"), true);
assert.equal(main.rootClasses.has("dream-art-standard"), true);
assert.equal(main.rootClasses.has("dream-task-ambient"), true);
assert.equal(main.routeClasses.has("dream-task"), true);
assert.equal(main.context.window.__CODEX_DREAM_SKIN_STATE__.cleanup(), true);
assert.equal(main.rootClasses.has("codex-dream-skin"), false);
assert.equal(main.rootClasses.has("dream-theme-dark"), false);
assert.equal(main.nodes.has("codex-dream-skin-style"), false);
assert.equal(main.nodes.has("codex-dream-skin-chrome"), false);
assert.deepEqual(main.revokedUrls, ["blob:fixture-1"]);

const reinjected = createFixture({ shellPresent: true });
vm.runInNewContext(payload, reinjected.context);
const firstState = reinjected.context.window.__CODEX_DREAM_SKIN_STATE__;
vm.runInNewContext(payload, reinjected.context);
const secondState = reinjected.context.window.__CODEX_DREAM_SKIN_STATE__;
assert.notEqual(secondState.installToken, firstState.installToken);
assert.equal(secondState.artUrl, "blob:fixture-2");
assert.equal(reinjected.rootStyles.get("--dream-art"), 'url("blob:fixture-2")');
assert.deepEqual(reinjected.revokedUrls, ["blob:fixture-1"]);
assert.equal(firstState.cleanup(), false);
assert.equal(secondState.cleanup(), true);

const auxiliary = createFixture({ shellPresent: false, staleSkin: true });
const auxiliaryResult = vm.runInNewContext(payload, auxiliary.context);
assert.equal(auxiliaryResult.installed, true);
assert.equal(auxiliary.rootClasses.has("codex-dream-skin"), false);
assert.equal(auxiliary.rootStyles.has("--dream-art"), false);
assert.equal(auxiliary.nodes.has("codex-dream-skin-style"), false);
assert.equal(auxiliary.nodes.has("codex-dream-skin-chrome"), false);

auxiliary.setShellPresent(true);
auxiliary.context.window.__CODEX_DREAM_SKIN_STATE__.ensure();
assert.equal(auxiliary.rootClasses.has("codex-dream-skin"), true);
assert.equal(auxiliary.nodes.has("codex-dream-skin-style"), true);
assert.equal(auxiliary.nodes.has("codex-dream-skin-chrome"), true);

const configured = createFixture({
  shellPresent: true,
  homePresent: true,
  utilityPresent: true,
  composerButtonLabel: "发送",
});
const configuredPayload = buildPayload({
  appearance: "light",
  palette: {
    accent: "#d45a70",
    canvas: "#f2d49a",
    surface: "#f7e0ad",
    text: "#3b2818",
  },
  controls: { profile: "railgun" },
  ui: { artVisibility: "high" },
  art: { focusX: .15, focusY: .8, safeArea: "right", taskMode: "off" },
});
const configuredResult = vm.runInNewContext(configuredPayload, configured.context);
assert.equal(configuredResult.adaptive, true);
assert.equal(configured.rootClasses.has("dream-theme-light"), true);
assert.equal(configured.rootClasses.has("dream-theme-dark"), false);
assert.equal(configured.rootClasses.has("dream-focus-left"), true);
assert.equal(configured.rootClasses.has("dream-safe-right"), true);
assert.equal(configured.rootClasses.has("dream-task-off"), true);
assert.equal(configured.rootStyles.get("--dream-art-position"), "15% 80%");
assert.equal(configured.rootStyles.get("--dream-accent"), "#d45a70");
assert.equal(configured.rootStyles.get("--dream-canvas"), "#f2d49a");
assert.equal(configured.rootStyles.get("--dream-surface"), "#f7e0ad");
assert.equal(configured.rootStyles.get("--dream-text"), "#3b2818");
assert.equal(configured.rootClasses.has("dream-art-visibility-high"), true);
assert.equal(configured.rootClasses.has("dream-controls-railgun"), true);
assert.equal(configured.composerButtonClasses.has("dream-send-lightning"), true);
assert.equal(configured.routeClasses.has("dream-home"), true);
assert.equal(configured.routeClasses.has("dream-task"), false);
assert.equal(configured.utilityClasses.has("dream-home-utility"), true);
assert.equal(configured.context.window.__CODEX_DREAM_SKIN_STATE__.cleanup(), true);
assert.equal(configured.rootClasses.has("dream-controls-railgun"), false);
assert.equal(configured.composerButtonClasses.has("dream-send-lightning"), false);
assert.equal(configured.utilityClasses.has("dream-home-utility"), false);

const busyComposer = createFixture({ shellPresent: true, composerButtonLabel: "停止" });
vm.runInNewContext(configuredPayload, busyComposer.context);
assert.equal(busyComposer.composerButtonClasses.has("dream-send-lightning"), false);

const clearArt = createFixture({ shellPresent: true });
vm.runInNewContext(buildPayload({
  appearance: "light",
  ui: { artVisibility: "clear" },
}), clearArt.context);
assert.equal(clearArt.rootClasses.has("dream-art-visibility-clear"), true);
assert.equal(clearArt.rootClasses.has("dream-art-visibility-high"), false);
assert.equal(clearArt.context.window.__CODEX_DREAM_SKIN_STATE__.cleanup(), true);
assert.equal(clearArt.rootClasses.has("dream-art-visibility-clear"), false);

const fullscreen = createFixture({ shellPresent: true, sidebarPresent: true });
vm.runInNewContext(buildPayload({
  appearance: "light",
  modes: {
    fullscreen: {
      image: "assets/background-fullscreen-2560x1440.png",
      art: { focusX: .76, focusY: .48 },
      artMetadata: { ratio: 16 / 9 },
    },
  },
}, "data:image/png;base64,AA=="), fullscreen.context);
assert.equal(fullscreen.rootClasses.has("codex-dream-skin"), true);
assert.equal(fullscreen.rootClasses.has("dream-mode-fullscreen"), false);
assert.equal(fullscreen.rootStyles.get("--dream-art"), 'url("blob:fixture-1")');
assert.equal(fullscreen.context.window.__CODEX_DREAM_SKIN_STATE__.mode, "standard");
fullscreen.setSidebarPresent(false);
fullscreen.context.window.__CODEX_DREAM_SKIN_STATE__.ensure();
assert.equal(fullscreen.rootClasses.has("dream-mode-fullscreen"), true);
assert.equal(fullscreen.rootStyles.get("--dream-art"), 'url("blob:fixture-2")');
assert.equal(fullscreen.context.window.__CODEX_DREAM_SKIN_STATE__.mode, "fullscreen");
fullscreen.setSidebarPresent(true);
fullscreen.context.window.__CODEX_DREAM_SKIN_STATE__.ensure();
assert.equal(fullscreen.rootClasses.has("dream-mode-fullscreen"), false);
assert.equal(fullscreen.rootStyles.get("--dream-art"), 'url("blob:fixture-1")');
assert.equal(fullscreen.context.window.__CODEX_DREAM_SKIN_STATE__.mode, "standard");
assert.equal(fullscreen.context.window.__CODEX_DREAM_SKIN_STATE__.cleanup(), true);
assert.deepEqual(fullscreen.revokedUrls, ["blob:fixture-1", "blob:fixture-2"]);

const analysisPixels = new Uint8ClampedArray(48 * 12 * 4);
for (let index = 0; index < 48 * 12; index += 1) {
  const offset = index * 4;
  const x = index % 48;
  const subject = x >= 34 && x <= 42;
  analysisPixels[offset] = subject ? 210 : 246;
  analysisPixels[offset + 1] = subject ? 84 : 239;
  analysisPixels[offset + 2] = subject ? 112 : 237;
  analysisPixels[offset + 3] = 255;
}
const analyzed = createFixture({
  shellPresent: true,
  analysisFixture: { naturalWidth: 1200, naturalHeight: 400, pixels: analysisPixels },
});
vm.runInNewContext(payload, analyzed.context);
await Promise.resolve();
assert.equal(analyzed.rootClasses.has("dream-theme-dark"), true);
assert.equal(analyzed.rootClasses.has("dream-theme-light"), false);
assert.equal(analyzed.rootClasses.has("dream-art-wide"), true);
assert.equal(analyzed.rootClasses.has("dream-task-banner"), true);
assert.equal(analyzed.rootClasses.has("dream-safe-left"), true);
assert.notEqual(analyzed.rootStyles.get("--dream-accent"), "rgb(216 104 119)");

const standardArt = createFixture({
  shellPresent: true,
  analysisFixture: { naturalWidth: 800, naturalHeight: 800, pixels: analysisPixels },
});
vm.runInNewContext(payload, standardArt.context);
await Promise.resolve();
assert.equal(standardArt.rootClasses.has("dream-art-standard"), true);
assert.equal(standardArt.rootClasses.has("dream-task-ambient"), true);
assert.equal(standardArt.rootClasses.has("dream-task-banner"), false);

const mediumWide = createFixture({
  shellPresent: true,
  analysisFixture: { naturalWidth: 2100, naturalHeight: 1000, pixels: analysisPixels },
});
vm.runInNewContext(payload, mediumWide.context);
await Promise.resolve();
assert.equal(mediumWide.rootClasses.has("dream-art-wide"), true);
assert.equal(mediumWide.rootClasses.has("dream-task-ambient"), true);
assert.equal(mediumWide.rootClasses.has("dream-task-banner"), false);

const nativeLight = createFixture({ shellPresent: true, shellAppearance: "light" });
vm.runInNewContext(payload, nativeLight.context);
assert.equal(nativeLight.rootClasses.has("dream-theme-light"), true);
assert.equal(nativeLight.rootClasses.has("dream-theme-dark"), false);

const nativeComputedDark = createFixture({
  shellPresent: true,
  shellAppearance: "",
  computedColorScheme: "dark",
  osAppearance: "light",
});
vm.runInNewContext(payload, nativeComputedDark.context);
assert.equal(nativeComputedDark.rootClasses.has("dream-theme-dark"), true);
assert.equal(nativeComputedDark.rootClasses.has("dream-theme-light"), false);
nativeComputedDark.context.window.__CODEX_DREAM_SKIN_STATE__.ensure();
assert.equal(nativeComputedDark.rootClasses.has("dream-theme-dark"), true);
const nativeObserver = nativeComputedDark.observers[0];
nativeObserver.takeRecords();
nativeComputedDark.context.window.__CODEX_DREAM_SKIN_STATE__.ensure();
assert.equal(nativeObserver.takeRecords().length, 0,
  "Sampling the native computed color-scheme must not queue a self-triggering root mutation pass.");

const metadataWide = createFixture({ shellPresent: true });
vm.runInNewContext(buildPayload({ artMetadata: { ratio: 16 / 9 } }), metadataWide.context);
assert.equal(metadataWide.rootClasses.has("dream-art-wide"), true);
assert.equal(metadataWide.rootClasses.has("dream-art-standard"), false);

const motionToken = "a".repeat(64);
const videoMotion = createFixture({ shellPresent: true });
vm.runInNewContext(buildPayload({ motion: { kind: "video", revision: "1".repeat(16), token: motionToken } }), videoMotion.context);
const videoElement = videoMotion.nodes.get("codex-dream-skin-motion");
assert.equal(videoElement.tagName, "VIDEO");
assert.match(videoElement.src, /127\.0\.0\.1:47866\/video\?t=/);
assert.equal(videoElement.muted, true);
videoElement.listeners.canplay();
assert.equal(videoMotion.rootClasses.has("dream-motion-ready"), true);
videoElement.listeners.error();
assert.equal(videoMotion.rootClasses.has("dream-motion-ready"), false);
const retry = videoMotion.timeouts.at(-1);
assert.equal(retry.delay, 500);
retry.callback();
assert.match(videoElement.src, /attempt=1/);
videoMotion.context.window.__CODEX_DREAM_SKIN_STATE__.cleanup();
assert.equal(videoMotion.nodes.has("codex-dream-skin-motion"), false);
const retiredSource = videoElement.src;
retry.callback();
assert.equal(videoElement.src, retiredSource, "a disposed media layer must not restart its stream");

const sceneMotion = createFixture({ shellPresent: true });
vm.runInNewContext(buildPayload({ motion: { kind: "scene", revision: "2".repeat(16), token: motionToken } }), sceneMotion.context);
const sceneElement = sceneMotion.nodes.get("codex-dream-skin-motion");
assert.equal(sceneElement.tagName, "IMG");
assert.match(sceneElement.src, /127\.0\.0\.1:47866\/scene\?t=/);
sceneElement.listeners.load();
assert.equal(sceneMotion.rootClasses.has("dream-motion-ready"), true);

const relayed = createFixture({ shellPresent: true });
relayed.context.Image = class {
  naturalWidth = 320;
  naturalHeight = 180;
  set src(value) { this.url = value; this.onload?.(); }
};
vm.runInNewContext(buildPayload({ motion: { kind: "video", revision: "3".repeat(16), transport: "cdp" } }), relayed.context);
const relayedState = relayed.context.window.__CODEX_DREAM_SKIN_STATE__;
const relayedElement = relayed.nodes.get("codex-dream-skin-motion");
assert.equal(relayedElement.tagName, "CANVAS");
assert.equal(relayedElement.src, undefined, "relayed playback must not open an HTTP media URL");
assert.equal(relayedState.acceptMotionFrame("AA==", "4".repeat(16)), false);
assert.equal(relayedState.acceptMotionFrame("AA==", "3".repeat(16)), true);
assert.match(relayedElement.__dreamDecoder.url, /^blob:/);
assert.equal(relayedState.motionFrames, 1);
assert.equal(relayedState.acceptMotionFrame("AQ==", "3".repeat(16)), true);
assert.equal(relayedState.motionFrames, 2);
assert.equal(relayed.rootClasses.has("dream-motion-ready"), true);

// React can retain both chats and only change their wrapper visibility.
// A -> B -> A must move the styling without replacing the shared video canvas.
const routeObserver = relayed.observers[0];
assert.ok(routeObserver.options.attributeFilter.includes("style"));
assert.ok(routeObserver.options.attributeFilter.includes("hidden"));
relayed.showCachedShell(1);
const pendingBefore = relayed.timeouts.length;
routeObserver.callback([{ attributeName: "style", target: { matches: () => true } }]);
routeObserver.callback([{ type: "childList", target: {} }]);
assert.equal(relayed.timeouts.length, pendingBefore + 1, "streaming mutations must not postpone a route update");
relayed.timeouts.at(-1).callback();
assert.equal(relayed.shellMain.classList.contains("dream-shell-main"), false);
assert.equal(relayed.secondShell.classList.contains("dream-shell-main"), true);
assert.equal(relayed.nodes.get("codex-dream-skin-motion"), relayedElement);
assert.equal(relayedState.acceptMotionFrame("Ag==", "3".repeat(16)), true);
assert.equal(relayedState.motionFrames, 3);
relayed.showCachedShell(-1);
relayedState.ensure();
assert.equal(relayed.nodes.get("codex-dream-skin-motion"), relayedElement, "a transient hidden page must keep its media");
relayed.showCachedShell(0);
relayedState.ensure();
assert.equal(relayed.shellMain.classList.contains("dream-shell-main"), true);
assert.equal(relayed.secondShell.classList.contains("dream-shell-main"), false);
assert.equal(relayed.nodes.get("codex-dream-skin-motion"), relayedElement);
assert.equal(relayed.rootClasses.has("dream-motion-ready"), true);
const afterRoute = relayed.timeouts.length;
routeObserver.callback([{ attributeName: "style", target: relayed.context.document.documentElement }]);
assert.equal(relayed.timeouts.length, afterRoute, "palette writes must not trigger route work");
relayedState.cleanup();
assert.equal(relayed.nodes.has("codex-dream-skin-motion"), false);
assert.equal(relayedState.acceptMotionFrame("AA==", "3".repeat(16)), false);

console.log("PASS: renderer applies adaptive theme metadata and preserves transparent auxiliary windows.");
