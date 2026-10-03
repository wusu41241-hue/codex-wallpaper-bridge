import fs from "node:fs/promises";
import { createHash } from "node:crypto";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { readImageMetadata } from "./image-metadata.mjs";
import { MotionRelay } from "./motion-relay.mjs";

const scriptPath = fileURLToPath(import.meta.url);
const here = path.dirname(scriptPath);
const root = path.resolve(here, "..");
const SKIN_VERSION = "1.5.3";
const MAX_ART_BYTES = 16 * 1024 * 1024;
const STRONG_THEME_AUDIT_MS = 30000;
const LOOPBACK_HOSTS = new Set(["127.0.0.1", "localhost", "[::1]", "::1"]);
const CODEX_RENDERER_TYPES = new Set(["page", "webview", "iframe"]);
const BROWSER_ID_PATTERN = /^[A-Za-z0-9._-]{1,200}$/;
const FORBIDDEN_CDP_METHOD = /^(Debugger|Input|Emulation|Overlay|HeapProfiler|Profiler|Page\.navigate)\./;
export const CODEX_SELECTORS = {
  shell: "main.main-surface, main[data-app-shell-main-surface], [data-app-shell-main-surface]",
  sidebar: 'aside.app-shell-left-panel, aside[data-testid="app-shell-floating-left-panel"], aside[data-app-shell-left-panel-appearance], .app-shell-left-panel',
  composer: ".composer-surface-chrome, [data-codex-composer-root], [data-codex-composer]",
  main: '[role="main"], [data-app-shell-main-content-layout], .messaging-root.messaging-embedded',
  pet: "[data-codex-pet-id]",
  home: '[role="main"]:has([data-testid="home-icon"]), [role="main"]:has([data-home-ambient-suggestions]), [data-home-ambient-suggestions]',
};

class CdpIdentityMismatchError extends Error {}

function parseArgs(argv) {
  const options = {
    port: 9335,
    mode: "watch",
    timeoutMs: 30000,
    screenshot: null,
    reload: false,
    browserId: null,
    themeDir: path.join(root, "assets"),
    pauseFile: null,
  };
  for (let i = 0; i < argv.length; i += 1) {
    const arg = argv[i];
    if (arg === "--port") options.port = Number(argv[++i]);
    else if (arg === "--once") options.mode = "once";
    else if (arg === "--watch") options.mode = "watch";
    else if (arg === "--verify") options.mode = "verify";
    else if (arg === "--remove") options.mode = "remove";
    else if (arg === "--timeout-ms") options.timeoutMs = Number(argv[++i]);
    else if (arg === "--browser-id") options.browserId = argv[++i];
    else if (arg === "--theme-dir") options.themeDir = path.resolve(argv[++i]);
    else if (arg === "--pause-file") options.pauseFile = path.resolve(argv[++i]);
    else if (arg === "--screenshot") options.screenshot = path.resolve(argv[++i]);
    else if (arg === "--reload") options.reload = true;
    else if (arg === "--self-test") options.mode = "self-test";
    else if (arg === "--check-payload") options.mode = "check-payload";
    else throw new Error(`Unknown argument: ${arg}`);
  }
  if (!Number.isInteger(options.port) || options.port < 1024 || options.port > 65535) {
    throw new Error(`Invalid port: ${options.port}`);
  }
  if (!Number.isInteger(options.timeoutMs) || options.timeoutMs < 250 || options.timeoutMs > 120000) {
    throw new Error(`Invalid timeout: ${options.timeoutMs}`);
  }
  if (options.browserId !== null && !BROWSER_ID_PATTERN.test(options.browserId)) {
    throw new Error(`Invalid browser ID: ${options.browserId}`);
  }
  if (["watch", "once", "verify", "remove"].includes(options.mode) && !options.browserId) {
    throw new Error(`--browser-id is required in ${options.mode} mode`);
  }
  return options;
}

function validatedDebuggerUrl(target, port) {
  const url = new URL(target.webSocketDebuggerUrl);
  const pathIsValid = /^\/devtools\/(?:page|browser)\/[A-Za-z0-9._-]{1,200}$/.test(url.pathname);
  if (url.protocol !== "ws:" || !LOOPBACK_HOSTS.has(url.hostname) || Number(url.port) !== port ||
      url.username || url.password || url.search || url.hash || !pathIsValid) {
    throw new Error("Rejected a CDP WebSocket URL outside the allowed loopback endpoint shape");
  }
  return url.href;
}

function parseCdpMessage(data) {
  try {
    const message = JSON.parse(String(data));
    return message && typeof message === "object" ? message : null;
  } catch {
    return null;
  }
}

function browserIdFromVersion(version, port) {
  const url = validatedDebuggerUrl(version, port);
  const parsed = new URL(url);
  const match = parsed.pathname.match(/^\/devtools\/browser\/([A-Za-z0-9._-]{1,200})$/);
  if (!match || parsed.search || parsed.hash || !BROWSER_ID_PATTERN.test(match[1])) {
    throw new Error("Rejected an invalid CDP browser identity URL");
  }
  return match[1];
}

function isTrustedCodexRendererUrl(value) {
  try {
    const url = new URL(value);
    if (url.protocol === "app:") return true;
    const host = url.hostname.toLowerCase();
    return url.protocol === "https:" && (
      host === "chatgpt.com" || host.endsWith(".chatgpt.com") ||
      host === "openai.com" || host.endsWith(".openai.com")
    );
  } catch {
    return false;
  }
}

function safeTargetLabel(item) {
  try {
    const url = new URL(item?.url ?? "");
    return `${item?.type ?? "unknown"}@${url.protocol}//${url.hostname || "local"}`;
  } catch {
    return `${item?.type ?? "unknown"}@invalid-url`;
  }
}

const FIND_CODEX_WINDOW_SOURCE = `(() => {
  const queue = [{ win: window, depth: 0 }];
  const visited = new Set();
  while (queue.length) {
    const current = queue.shift();
    if (!current?.win || visited.has(current.win)) continue;
    visited.add(current.win);
    try {
      const doc = current.win.document;
      const pet = doc.querySelector(${JSON.stringify(CODEX_SELECTORS.pet)});
      const shell = doc.querySelector(${JSON.stringify(CODEX_SELECTORS.shell)});
      if (pet && !shell) continue;
      const content = doc.querySelector(${JSON.stringify(CODEX_SELECTORS.composer)}) ||
        doc.querySelector(${JSON.stringify(CODEX_SELECTORS.main)});
      if (shell && content) return current.win;
      if (current.depth >= 4) continue;
      for (const frame of doc.querySelectorAll('iframe')) {
        try {
          if (frame.contentWindow?.document) queue.push({ win: frame.contentWindow, depth: current.depth + 1 });
        } catch {}
      }
    } catch {}
  }
  return null;
})()`;

function inCodexWindow(expression) {
  return `(() => {
    const targetWindow = ${FIND_CODEX_WINDOW_SOURCE};
    if (!targetWindow) throw new Error('No trusted Codex document context is available');
    return targetWindow.eval(${JSON.stringify(expression)});
  })()`;
}

function isValidCdpRendererTarget(item, port) {
  if (!CODEX_RENDERER_TYPES.has(item?.type) || !isTrustedCodexRendererUrl(item?.url) ||
      typeof item.id !== "string" ||
      !BROWSER_ID_PATTERN.test(item.id) || !item.webSocketDebuggerUrl) return false;
  try {
    const debuggerUrl = new URL(validatedDebuggerUrl(item, port));
    return debuggerUrl.pathname === `/devtools/page/${item.id}`;
  } catch {
    return false;
  }
}

class CdpSession {
  constructor(target, port) {
    this.target = target;
    this.ws = new WebSocket(validatedDebuggerUrl(target, port));
    this.nextId = 1;
    this.pending = new Map();
    this.listeners = new Map();
    this.closed = false;
  }

  async open() {
    await new Promise((resolve, reject) => {
      const timeout = setTimeout(() => {
        try { this.ws.close(); } catch {}
        reject(new Error("CDP WebSocket open timed out"));
      }, 5000);
      this.ws.addEventListener("open", () => { clearTimeout(timeout); resolve(); }, { once: true });
      this.ws.addEventListener("error", () => { clearTimeout(timeout); reject(new Error("CDP WebSocket open failed")); }, { once: true });
    });
    this.ws.addEventListener("message", (event) => this.onMessage(event));
    this.ws.addEventListener("error", () => this.close());
    this.ws.addEventListener("close", () => {
      this.closed = true;
      for (const waiter of this.pending.values()) {
        clearTimeout(waiter.timeout);
        waiter.reject(new Error("CDP socket closed"));
      }
      this.pending.clear();
    });
    await this.send("Runtime.enable");
    await this.send("Page.enable");
    return this;
  }

  onMessage(event) {
    const message = parseCdpMessage(event.data);
    if (!message) {
      this.close();
      return;
    }
    if (message.id) {
      const waiter = this.pending.get(message.id);
      if (!waiter) return;
      clearTimeout(waiter.timeout);
      this.pending.delete(message.id);
      if (message.error) waiter.reject(new Error(`${message.error.message} (${message.error.code})`));
      else waiter.resolve(message.result);
      return;
    }
    for (const listener of this.listeners.get(message.method) ?? []) listener(message.params ?? {});
  }

  on(method, listener) {
    const listeners = this.listeners.get(method) ?? [];
    listeners.push(listener);
    this.listeners.set(method, listeners);
  }

  send(method, params = {}) {
    if (this.closed) return Promise.reject(new Error("CDP session is closed"));
    if (FORBIDDEN_CDP_METHOD.test(method)) {
      return Promise.reject(new Error(`Refusing CDP method that can crash Codex: ${method}`));
    }
    return new Promise((resolve, reject) => {
      const id = this.nextId++;
      const timeout = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`CDP command timed out: ${method}`));
      }, 10000);
      this.pending.set(id, { resolve, reject, timeout });
      try {
        this.ws.send(JSON.stringify({ id, method, params }));
      } catch (error) {
        clearTimeout(timeout);
        this.pending.delete(id);
        reject(error);
      }
    });
  }

  async evaluate(expression) {
    const result = await this.send("Runtime.evaluate", {
      expression,
      awaitPromise: true,
      returnByValue: true,
      userGesture: false,
    });
    if (result.exceptionDetails) {
      const detail = result.exceptionDetails.exception?.description ?? result.exceptionDetails.text;
      throw new Error(`Renderer evaluation failed: ${detail}`);
    }
    return result.result?.value;
  }

  close() {
    for (const waiter of this.pending.values()) {
      clearTimeout(waiter.timeout);
      waiter.reject(new Error("CDP session closed"));
    }
    this.pending.clear();
    if (!this.closed) {
      try { this.ws.close(); } catch {}
    }
    this.closed = true;
  }
}

class BrowserIdentityAnchor {
  constructor(url) {
    this.ws = new WebSocket(url);
    this.closed = false;
    this.ws.addEventListener("close", () => { this.closed = true; });
    this.ws.addEventListener("error", () => {
      this.closed = true;
      try { this.ws.close(); } catch {}
    });
  }

  async open() {
    await new Promise((resolve, reject) => {
      const timeout = setTimeout(() => {
        this.close();
        reject(new Error("CDP browser identity WebSocket open timed out"));
      }, 5000);
      this.ws.addEventListener("open", () => { clearTimeout(timeout); resolve(); }, { once: true });
      this.ws.addEventListener("error", () => {
        clearTimeout(timeout);
        reject(new Error("CDP browser identity WebSocket open failed"));
      }, { once: true });
      this.ws.addEventListener("close", () => {
        clearTimeout(timeout);
        reject(new Error("CDP browser identity WebSocket closed during startup"));
      }, { once: true });
    });
    if (this.closed) throw new Error("CDP browser identity WebSocket is already closed");
    return this;
  }

  close() {
    if (!this.closed) {
      try { this.ws.close(); } catch {}
    }
    this.closed = true;
  }
}

async function fetchCdpJson(port, resource) {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 2000);
  try {
    const response = await fetch(`http://127.0.0.1:${port}${resource}`, {
      redirect: "error",
      signal: controller.signal,
    });
    if (!response.ok) throw new Error(`HTTP ${response.status}`);
    return await response.json();
  } finally {
    clearTimeout(timeout);
  }
}

async function listAppTargets(port, expectedBrowserId = null) {
  const targets = await fetchCdpJson(port, "/json/list");
  if (!Array.isArray(targets)) throw new Error("CDP target list is not an array");
  if (expectedBrowserId) {
    const version = await fetchCdpJson(port, "/json/version");
    const actualBrowserId = browserIdFromVersion(version, port);
    if (actualBrowserId !== expectedBrowserId) {
      throw new CdpIdentityMismatchError(
        `CDP browser identity changed from ${expectedBrowserId} to ${actualBrowserId}`,
      );
    }
  }
  return targets.filter((item) => isValidCdpRendererTarget(item, port));
}

async function connectBrowserIdentityAnchor(port, expectedBrowserId) {
  const version = await fetchCdpJson(port, "/json/version");
  const actualBrowserId = browserIdFromVersion(version, port);
  if (actualBrowserId !== expectedBrowserId) {
    throw new CdpIdentityMismatchError(
      `CDP browser identity changed from ${expectedBrowserId} to ${actualBrowserId}`,
    );
  }
  return new BrowserIdentityAnchor(validatedDebuggerUrl(version, port)).open();
}

const THEME_CHOICES = {
  appearance: new Set(["auto", "light", "dark"]),
  safeArea: new Set(["auto", "left", "right", "center", "none"]),
  taskMode: new Set(["auto", "ambient", "banner", "off"]),
  controlProfile: new Set(["standard", "railgun"]),
  artVisibility: new Set(["normal", "high", "clear"]),
};

const THEME_PALETTE_FIELDS = new Set([
  "accent", "canvas", "surface", "surfaceRaised", "sidebar", "text", "textMuted", "line",
]);
const THEME_COLOR_PATTERN = /^(?:#[\da-f]{3,8}|(?:rgb|hsl|oklch|oklab)\([^;{}]{1,96}\))$/i;

function normalizedUnit(value, name) {
  if (value === null || value === undefined || value === "") return null;
  const number = Number(value);
  if (!Number.isFinite(number) || number < 0 || number > 1) {
    throw new Error(`${name} must be null or a number between 0 and 1`);
  }
  return number;
}

function normalizedChoice(value, name, choices, fallback) {
  if (value === null || value === undefined || value === "") return fallback;
  if (!choices.has(value)) throw new Error(`${name} has an unsupported value: ${value}`);
  return value;
}

function normalizedText(value, name, fallback, maxLength = 120) {
  if (value === null || value === undefined || value === "") return fallback;
  if (typeof value !== "string" || value.length > maxLength || /[\u0000-\u001f]/.test(value)) {
    throw new Error(`${name} must be a short single-line string`);
  }
  return value;
}

async function loadTheme(themeDir) {
  const realThemeDir = await fs.realpath(themeDir);
  const themePath = path.join(realThemeDir, "theme.json");
  const themeText = await fs.readFile(themePath, "utf8");
  const raw = JSON.parse(themeText);
  if (!raw || typeof raw !== "object" || Array.isArray(raw)) {
    throw new Error("Theme root must be an object");
  }
  const image = normalizedText(raw.image, "image", null, 240);
  if (!image || path.isAbsolute(image)) throw new Error("Theme image must be a relative path");
  const imagePath = path.resolve(realThemeDir, image);
  const relativeImage = path.relative(realThemeDir, imagePath);
  if (!relativeImage || relativeImage.startsWith("..") || path.isAbsolute(relativeImage)) {
    throw new Error("Theme image must remain inside the selected theme directory");
  }
  const extension = path.extname(imagePath).toLowerCase();
  if (![".png", ".jpg", ".jpeg", ".webp"].includes(extension)) {
    throw new Error(`Unsupported theme image format: ${extension || "missing"}`);
  }
  const realImagePath = await fs.realpath(imagePath);
  const realRelativeImage = path.relative(realThemeDir, realImagePath);
  if (!realRelativeImage || realRelativeImage.startsWith("..") || path.isAbsolute(realRelativeImage)) {
    throw new Error("Theme image cannot escape through a link or junction");
  }
  let motionPath = null;
  let motionStat = null;
  let motionKind = null;
  if (raw.motion !== undefined) {
    if (!raw.motion || typeof raw.motion !== "object" || Array.isArray(raw.motion)) {
      throw new Error("motion must be an object");
    }
    motionKind = normalizedChoice(raw.motion.kind, "motion.kind", new Set(["video", "scene", "web"]), null);
    const requested = normalizedText(raw.motion.source, "motion.source", null, 1024);
    if (!motionKind || !requested || !path.isAbsolute(requested)) throw new Error("motion requires an absolute Wallpaper Engine source");
    motionPath = await fs.realpath(requested);
    let allowedRoots = null;
    for (const parent of [path.dirname(themeDir), path.dirname(path.dirname(themeDir))]) {
      const configFile = path.join(parent, "control", "wallpaper-roots.json");
      if (await fileExists(configFile)) { allowedRoots = JSON.parse(await fs.readFile(configFile, "utf8")); break; }
    }
    if (!Array.isArray(allowedRoots) || allowedRoots.length < 1 || allowedRoots.length > 16 ||
        allowedRoots.some((value) => typeof value !== "string" || !path.isAbsolute(value))) {
      throw new Error("Wallpaper Engine library roots are missing; import the wallpaper again");
    }
    allowedRoots = (await Promise.all(allowedRoots.map((value) => fs.realpath(value).catch(() => null)))).filter(Boolean);
    if (!allowedRoots.some((allowed) => {
      const relative = path.relative(allowed, motionPath);
      return relative && !relative.startsWith("..") && !path.isAbsolute(relative);
    })) throw new Error("motion source is outside Wallpaper Engine project directories");
    const motionExtension = path.extname(motionPath).toLowerCase();
    if (motionKind === "video" && ![".mp4", ".webm", ".m4v"].includes(motionExtension)) {
      throw new Error("motion video format is unsupported");
    }
    if (motionKind !== "video" && path.basename(motionPath).toLowerCase() !== "project.json") {
      throw new Error("motion scene source must be a Wallpaper Engine project.json");
    }
    motionStat = await fs.stat(motionPath);
    if (!motionStat.isFile() || motionStat.size < 1) throw new Error("motion source is not a regular file");
  }
  const modes = raw.modes && typeof raw.modes === "object" && !Array.isArray(raw.modes) ? raw.modes : {};
  for (const key of Object.keys(modes)) {
    if (key !== "fullscreen") throw new Error(`modes has an unsupported field: ${key}`);
  }
  const fullscreen = modes.fullscreen && typeof modes.fullscreen === "object" && !Array.isArray(modes.fullscreen)
    ? modes.fullscreen : {};
  for (const key of Object.keys(fullscreen)) {
    if (!new Set(["image", "art"]).has(key)) throw new Error(`modes.fullscreen has an unsupported field: ${key}`);
  }
  const fullscreenArt = fullscreen.art && typeof fullscreen.art === "object" && !Array.isArray(fullscreen.art)
    ? fullscreen.art : {};
  for (const key of Object.keys(fullscreenArt)) {
    if (!new Set(["focusX", "focusY"]).has(key)) {
      throw new Error(`modes.fullscreen.art has an unsupported field: ${key}`);
    }
  }
  const fullscreenImage = normalizedText(fullscreen.image, "modes.fullscreen.image", null, 240);
  let realFullscreenImagePath = null;
  let fullscreenExtension = null;
  if (fullscreenImage) {
    if (path.isAbsolute(fullscreenImage)) throw new Error("Fullscreen theme image must be a relative path");
    const fullscreenImagePath = path.resolve(realThemeDir, fullscreenImage);
    const relativeFullscreenImage = path.relative(realThemeDir, fullscreenImagePath);
    if (!relativeFullscreenImage || relativeFullscreenImage.startsWith("..") || path.isAbsolute(relativeFullscreenImage)) {
      throw new Error("Fullscreen theme image must remain inside the selected theme directory");
    }
    fullscreenExtension = path.extname(fullscreenImagePath).toLowerCase();
    if (![".png", ".jpg", ".jpeg", ".webp"].includes(fullscreenExtension)) {
      throw new Error(`Unsupported fullscreen theme image format: ${fullscreenExtension || "missing"}`);
    }
    realFullscreenImagePath = await fs.realpath(fullscreenImagePath);
    const realRelativeFullscreenImage = path.relative(realThemeDir, realFullscreenImagePath);
    if (!realRelativeFullscreenImage || realRelativeFullscreenImage.startsWith("..") || path.isAbsolute(realRelativeFullscreenImage)) {
      throw new Error("Fullscreen theme image cannot escape through a link or junction");
    }
  }
  const art = raw.art && typeof raw.art === "object" && !Array.isArray(raw.art) ? raw.art : {};
  const palette = raw.palette && typeof raw.palette === "object" && !Array.isArray(raw.palette)
    ? raw.palette : {};
  const controls = raw.controls && typeof raw.controls === "object" && !Array.isArray(raw.controls)
    ? raw.controls : {};
  const ui = raw.ui && typeof raw.ui === "object" && !Array.isArray(raw.ui) ? raw.ui : {};
  for (const key of Object.keys(palette)) {
    if (!THEME_PALETTE_FIELDS.has(key)) throw new Error(`palette has an unsupported field: ${key}`);
  }
  for (const key of Object.keys(controls)) {
    if (key !== "profile") throw new Error(`controls has an unsupported field: ${key}`);
  }
  for (const key of Object.keys(ui)) {
    if (key !== "artVisibility") throw new Error(`ui has an unsupported field: ${key}`);
  }
  const theme = {
    id: normalizedText(raw.id, "id", "custom", 80),
    name: normalizedText(raw.name, "name", "Codex Dream Skin", 120),
    image,
    appearance: normalizedChoice(raw.appearance, "appearance", THEME_CHOICES.appearance, "auto"),
    art: {
      focusX: normalizedUnit(art.focusX, "art.focusX"),
      focusY: normalizedUnit(art.focusY, "art.focusY"),
      safeArea: normalizedChoice(art.safeArea, "art.safeArea", THEME_CHOICES.safeArea, "auto"),
      taskMode: normalizedChoice(art.taskMode, "art.taskMode", THEME_CHOICES.taskMode, "auto"),
    },
    palette: {},
    controls: {
      profile: normalizedChoice(
        controls.profile, "controls.profile", THEME_CHOICES.controlProfile, "standard",
      ),
    },
    ui: {
      artVisibility: normalizedChoice(
        ui.artVisibility, "ui.artVisibility", THEME_CHOICES.artVisibility, "normal",
      ),
    },
    modes: {},
    motion: motionPath ? {
      kind: motionKind,
      revision: createHash("sha256").update(motionPath).update(String(motionStat.mtimeMs)).digest("hex").slice(0, 16),
    } : null,
  };
  if (fullscreenImage) {
    theme.modes.fullscreen = {
      image: fullscreenImage,
      art: {
        focusX: normalizedUnit(fullscreenArt.focusX, "modes.fullscreen.art.focusX"),
        focusY: normalizedUnit(fullscreenArt.focusY, "modes.fullscreen.art.focusY"),
      },
    };
  }
  for (const key of THEME_PALETTE_FIELDS) {
    if (typeof palette[key] === "string" && palette[key].trim()) {
      const color = palette[key].trim();
      if (!THEME_COLOR_PATTERN.test(color)) {
        throw new Error(`palette.${key} is not a supported CSS color`);
      }
      theme.palette[key] = color;
    }
  }
  const [themeStat, imageStat] = await Promise.all([fs.stat(themePath), fs.stat(realImagePath)]);
  if (!imageStat.isFile()) throw new Error("Theme image is not a file");
  if (imageStat.size < 1) throw new Error("Theme image cannot be empty");
  if (imageStat.size > MAX_ART_BYTES) {
    throw new Error(`Theme image exceeds the ${MAX_ART_BYTES / 1024 / 1024} MB limit`);
  }
  const imageBytes = await fs.readFile(realImagePath);
  if (imageBytes.length < 1 || imageBytes.length > MAX_ART_BYTES) {
    throw new Error(`Theme image must be between 1 byte and ${MAX_ART_BYTES / 1024 / 1024} MB`);
  }
  const artMetadata = readImageMetadata(imageBytes, extension);
  if (!artMetadata) {
    throw new Error("Theme image metadata is invalid or exceeds the 16384px / 50MP safety limit");
  }
  theme.artMetadata = artMetadata;
  let fullscreenImageBytes = null;
  let fullscreenImageStat = null;
  if (realFullscreenImagePath) {
    fullscreenImageStat = await fs.stat(realFullscreenImagePath);
    if (!fullscreenImageStat.isFile()) throw new Error("Fullscreen theme image is not a file");
    if (fullscreenImageStat.size < 1 || fullscreenImageStat.size > MAX_ART_BYTES) {
      throw new Error(`Fullscreen theme image must be between 1 byte and ${MAX_ART_BYTES / 1024 / 1024} MB`);
    }
    fullscreenImageBytes = await fs.readFile(realFullscreenImagePath);
    const fullscreenArtMetadata = readImageMetadata(fullscreenImageBytes, fullscreenExtension);
    if (!fullscreenArtMetadata) {
      throw new Error("Fullscreen theme image metadata is invalid or exceeds the 16384px / 50MP safety limit");
    }
    theme.modes.fullscreen.artMetadata = fullscreenArtMetadata;
  }
  const fingerprintHash = createHash("sha256")
    .update(themeText, "utf8")
    .update("\0")
    .update(imageBytes);
  if (fullscreenImageBytes) fingerprintHash.update("\0").update(fullscreenImageBytes);
  if (motionPath) fingerprintHash.update("\0").update(motionPath).update(String(motionStat.mtimeMs));
  const fingerprint = fingerprintHash.digest("hex");
  const sourceStampParts = [themeStat.size, themeStat.mtimeMs, imageStat.size, imageStat.mtimeMs];
  if (fullscreenImageStat) sourceStampParts.push(fullscreenImageStat.size, fullscreenImageStat.mtimeMs);
  if (motionStat) sourceStampParts.push(motionStat.size, motionStat.mtimeMs);
  return {
    theme,
    themePath,
    imagePath: realImagePath,
    imageBytes,
    fullscreenImagePath: realFullscreenImagePath,
    fullscreenImageBytes,
    motionPath,
    fingerprint,
    sourceStamp: sourceStampParts.join(":"),
  };
}

async function listTargetInventory(port) {
  const targets = await fetchCdpJson(port, "/json/list");
  if (!Array.isArray(targets)) return [];
  return targets.map((item) => safeTargetLabel(item)).slice(0, 24);
}

async function loadPayload(themeDir = path.join(root, "assets"), candidateTheme = null) {
  const loadedTheme = candidateTheme ?? await loadTheme(themeDir);
  const [css, template] = await Promise.all([
    fs.readFile(path.join(root, "assets", "dream-skin.css"), "utf8"),
    fs.readFile(path.join(root, "assets", "renderer-inject.js"), "utf8"),
  ]);
  const extension = path.extname(loadedTheme.imagePath).toLowerCase();
  const mime = extension === ".jpg" || extension === ".jpeg" ? "image/jpeg"
    : extension === ".webp" ? "image/webp" : "image/png";
  const artDataUrl = `data:${mime};base64,${loadedTheme.imageBytes.toString("base64")}`;
  let fullscreenArtDataUrl = null;
  if (loadedTheme.fullscreenImagePath && loadedTheme.fullscreenImageBytes) {
    const fullscreenExtension = path.extname(loadedTheme.fullscreenImagePath).toLowerCase();
    const fullscreenMime = fullscreenExtension === ".jpg" || fullscreenExtension === ".jpeg" ? "image/jpeg"
      : fullscreenExtension === ".webp" ? "image/webp" : "image/png";
    fullscreenArtDataUrl = `data:${fullscreenMime};base64,${loadedTheme.fullscreenImageBytes.toString("base64")}`;
  }
  let rendererTheme = loadedTheme.theme;
  let motionRelay = null;
  if (loadedTheme.theme.motion) {
    const tokenPaths = [
      path.join(path.dirname(themeDir), "control", "motion-token.txt"),
      path.join(path.dirname(path.dirname(themeDir)), "control", "motion-token.txt"),
    ];
    let token = null;
    for (const candidate of tokenPaths) {
      if (await fileExists(candidate)) { token = (await fs.readFile(candidate, "utf8")).trim(); break; }
    }
    if (!/^[a-f0-9]{64}$/.test(token)) throw new Error("Wallpaper motion token is invalid");
    rendererTheme = { ...loadedTheme.theme, motion: { ...loadedTheme.theme.motion, transport: "cdp" } };
    motionRelay = { key: loadedTheme.fingerprint, revision: loadedTheme.theme.motion.revision, token };
  }
  const payload = template
    .replace("__DREAM_CSS_JSON__", JSON.stringify(css))
    .replace("__DREAM_ART_JSON__", JSON.stringify(artDataUrl))
    .replace("__DREAM_FULLSCREEN_ART_JSON__", JSON.stringify(fullscreenArtDataUrl))
    .replace("__DREAM_THEME_JSON__", JSON.stringify(rendererTheme));
  const { imageBytes: _imageBytes, fullscreenImageBytes: _fullscreenImageBytes, ...themeState } = loadedTheme;
  return { ...themeState, payload, motionRelay };
}

async function fileExists(filePath) {
  if (!filePath) return false;
  try {
    return (await fs.stat(filePath)).isFile();
  } catch (error) {
    if (error?.code === "ENOENT") return false;
    throw error;
  }
}

async function readThemeSourceStamp(loadedTheme) {
  const [themeStat, imageStat, fullscreenImageStat, motionStat] = await Promise.all([
    fs.stat(loadedTheme.themePath),
    fs.stat(loadedTheme.imagePath),
    loadedTheme.fullscreenImagePath ? fs.stat(loadedTheme.fullscreenImagePath) : Promise.resolve(null),
    loadedTheme.motionPath ? fs.stat(loadedTheme.motionPath) : Promise.resolve(null),
  ]);
  const parts = [themeStat.size, themeStat.mtimeMs, imageStat.size, imageStat.mtimeMs];
  if (fullscreenImageStat) parts.push(fullscreenImageStat.size, fullscreenImageStat.mtimeMs);
  if (motionStat) parts.push(motionStat.size, motionStat.mtimeMs);
  return parts.join(":");
}

async function probeSession(session) {
  return session.evaluate(`(() => {
    const targetWindow = ${FIND_CODEX_WINDOW_SOURCE};
    const targetDocument = targetWindow?.document ?? document;
    const markers = {
      shell: Boolean(targetDocument.querySelector(${JSON.stringify(CODEX_SELECTORS.shell)})),
      sidebar: Boolean(targetDocument.querySelector(${JSON.stringify(CODEX_SELECTORS.sidebar)})),
      composer: Boolean(targetDocument.querySelector(${JSON.stringify(CODEX_SELECTORS.composer)})),
      main: Boolean(targetDocument.querySelector(${JSON.stringify(CODEX_SELECTORS.main)})),
      pet: Boolean(targetDocument.querySelector(${JSON.stringify(CODEX_SELECTORS.pet)})),
    };
    const targetLocation = targetWindow?.location ?? location;
    const host = targetLocation.hostname.toLowerCase();
    const trustedLocation = targetLocation.protocol === 'app:' ||
      (targetLocation.protocol === 'https:' && (
        host === 'chatgpt.com' || host.endsWith('.chatgpt.com') ||
        host === 'openai.com' || host.endsWith('.openai.com')
      ));
    return {
      markers,
      trustedLocation,
      embedded: Boolean(targetWindow && targetWindow !== window),
      codex: Boolean(targetWindow) && trustedLocation && !markers.pet &&
        markers.shell && (markers.composer || markers.main),
    };
  })()`);
}

async function waitForCodexProbe(session, timeoutMs = 1800) {
  const deadline = Date.now() + timeoutMs;
  let probe = null;
  while (Date.now() < deadline) {
    try {
      probe = await probeSession(session);
      if (probe?.codex) return probe;
    } catch {
      // The renderer may be between documents while the early payload waits.
    }
    await new Promise((resolve) => setTimeout(resolve, 50));
  }
  return probe;
}

async function connectTarget(target, port) {
  return new CdpSession(target, port).open();
}

async function connectCodexTargets(port, timeoutMs, expectedBrowserId) {
  const deadline = Date.now() + timeoutMs;
  let lastError;
  while (Date.now() < deadline) {
    try {
      const targets = await listAppTargets(port, expectedBrowserId);
      const connected = [];
      const diagnostics = [];
      for (const target of targets) {
        let session;
        try {
          session = await connectTarget(target, port);
          const probe = await probeSession(session);
          if (probe?.codex) connected.push({ target, session, probe });
          else {
            diagnostics.push(`${safeTargetLabel(target)} shell=${Number(Boolean(probe?.markers?.shell))}` +
              ` composer=${Number(Boolean(probe?.markers?.composer))}` +
              ` main=${Number(Boolean(probe?.markers?.main))}`);
            session.close();
          }
        } catch (error) {
          session?.close();
          lastError = error;
        }
      }
      if (connected.length) return connected;
      const inventory = await listTargetInventory(port).catch(() => []);
      lastError = new Error(`No trusted renderer matched the Codex shell markers` +
        (diagnostics.length
          ? ` (${diagnostics.slice(0, 8).join("; ")})`
          : " (no trusted page/webview/iframe targets)") +
        (inventory.length ? `; CDP inventory: ${inventory.join(", ")}` : ""));
    } catch (error) {
      if (error instanceof CdpIdentityMismatchError) throw error;
      lastError = error;
    }
    await new Promise((resolve) => setTimeout(resolve, 350));
  }
  throw new Error(`No verified Codex renderer on 127.0.0.1:${port}: ${lastError?.message ?? "timed out"}`);
}

async function applyToSession(session, payload) {
  return session.evaluate(inCodexWindow(payload));
}

export function earlyPayloadFor(payload, revision) {
  return `(() => {
    const generationKey = "__CODEX_DREAM_SKIN_EARLY_GENERATION__";
    const appliedKey = "__CODEX_DREAM_SKIN_EARLY_APPLIED__";
    const generation = ${JSON.stringify(revision)};
    window[generationKey] = generation;
    let observer = null;
    let timeout = null;
    let poll = null;
    const stop = () => {
      observer?.disconnect();
      observer = null;
      if (timeout) clearTimeout(timeout);
      timeout = null;
      if (poll) clearInterval(poll);
      poll = null;
    };
    const install = () => {
      if (window[generationKey] !== generation) { stop(); return true; }
      const targetWindow = ${FIND_CODEX_WINDOW_SOURCE};
      if (!targetWindow) return false;
      stop();
      try { targetWindow.eval(${JSON.stringify(payload)}); } catch { return false; }
      window[appliedKey] = generation;
      return true;
    };
    if (install()) return;
    if (typeof MutationObserver === "function" && document.documentElement) {
      observer = new MutationObserver(install);
      observer.observe(document.documentElement, { childList: true, subtree: true });
    }
    poll = setInterval(install, 250);
    timeout = setTimeout(stop, 60000);
  })()`;
}

async function registerEarlyPayload(session, payload, revision) {
  const result = await session.send("Page.addScriptToEvaluateOnNewDocument", {
    source: earlyPayloadFor(payload, revision),
  });
  return result.identifier ?? null;
}

async function removeEarlyPayload(session, identifier) {
  if (!identifier || session.closed) return;
  await session.send("Page.removeScriptToEvaluateOnNewDocument", { identifier }).catch(() => {});
}

async function removeFromSession(session) {
  const expression = `(() => {
    window.__CODEX_DREAM_SKIN_DISABLED__ = true;
    const state = window.__CODEX_DREAM_SKIN_STATE__;
    if (state?.cleanup) return state.cleanup();
    document.documentElement?.classList.remove(
      'codex-dream-skin', 'dream-theme-light', 'dream-theme-dark',
      'dream-art-wide', 'dream-art-standard', 'dream-focus-left',
      'dream-focus-center', 'dream-focus-right', 'dream-safe-left',
      'dream-safe-center', 'dream-safe-right', 'dream-safe-none',
      'dream-task-ambient', 'dream-task-banner', 'dream-task-off',
      'dream-art-visibility-high', 'dream-art-visibility-clear', 'dream-controls-railgun',
      'dream-mode-fullscreen', 'dream-motion-ready'
    );
    for (const property of [
      '--dream-art', '--dream-art-position', '--dream-focus-x', '--dream-focus-y',
      '--dream-accent', '--dream-accent-ink', '--dream-image-luma',
      '--dream-canvas', '--dream-surface', '--dream-surface-raised', '--dream-sidebar',
      '--dream-text', '--dream-text-muted', '--dream-line'
    ]) document.documentElement?.style.removeProperty(property);
    document.querySelectorAll('.dream-send-lightning').forEach((node) => node.classList.remove('dream-send-lightning'));
    document.querySelectorAll('.dream-home').forEach((node) => node.classList.remove('dream-home'));
    document.querySelectorAll('.dream-task').forEach((node) => node.classList.remove('dream-task'));
    document.querySelectorAll('.dream-home-shell').forEach((node) => node.classList.remove('dream-home-shell'));
    document.querySelectorAll('.dream-shell-main').forEach((node) => node.classList.remove('dream-shell-main'));
    document.querySelectorAll('.dream-shell-sidebar').forEach((node) => node.classList.remove('dream-shell-sidebar'));
    document.querySelectorAll('.dream-composer').forEach((node) => node.classList.remove('dream-composer'));
    document.getElementById('codex-dream-skin-style')?.remove();
    document.getElementById('codex-dream-skin-chrome')?.remove();
    const media = document.getElementById('codex-dream-skin-motion');
    if (media) {
      media.__dreamDisposed = true;
      if (media.__dreamRetry) clearTimeout(media.__dreamRetry);
      media.pause?.(); media.removeAttribute?.('src'); media.load?.(); media.remove();
    }
    delete window.__CODEX_DREAM_SKIN_STATE__;
    return true;
  })()`;
  return session.evaluate(inCodexWindow(expression));
}

async function verifyRemovedSession(session) {
  const expression = `(() =>
    !document.documentElement.classList.contains('codex-dream-skin') &&
    !document.documentElement.style.getPropertyValue('--dream-art') &&
    !document.querySelector('.dream-home') &&
    !document.querySelector('.dream-task') &&
    !document.querySelector('.dream-home-shell') &&
    !document.querySelector('.dream-shell-main') &&
    !document.querySelector('.dream-shell-sidebar') &&
    !document.querySelector('.dream-composer') &&
    !document.getElementById('codex-dream-skin-style') &&
    !document.getElementById('codex-dream-skin-chrome') &&
    !document.getElementById('codex-dream-skin-motion') &&
    !window.__CODEX_DREAM_SKIN_STATE__
  )()`;
  return session.evaluate(inCodexWindow(expression));
}

// Cached routes remain in DOM order before the visible route. A selector list
// does not prioritize its first selector, so inspect geometry before choosing.
export function visibleSkinNode(doc, selector) {
  const view = doc.defaultView;
  return [...doc.querySelectorAll(selector)].find(node => {
    const rect = node.getBoundingClientRect();
    const style = view?.getComputedStyle?.(node);
    return rect.width > 0 && rect.height > 0 && style?.display !== "none" &&
      style?.visibility !== "hidden" && (!view ||
        (rect.left < view.innerWidth && rect.top < view.innerHeight && rect.right > 0 && rect.bottom > 0));
  }) ?? null;
}

export function rendererHealthExpression(version = SKIN_VERSION) {
  return `(() => {
    const state = window.__CODEX_DREAM_SKIN_STATE__;
    if (!state || state.version !== ${JSON.stringify(version)} || window.__CODEX_DREAM_SKIN_DISABLED__) return false;
    state.ensure?.();
    return Boolean(document.getElementById('codex-dream-skin-style') &&
      document.getElementById('codex-dream-skin-chrome') &&
      (!state.config?.motion || document.getElementById('codex-dream-skin-motion')));
  })()`;
}

async function verifySession(session, expectedTheme = null) {
  const expression = `(() => {
    const visibleNode = ${visibleSkinNode.toString()};
    const activeShell = visibleNode(document, '.dream-shell-main') || visibleNode(document, ${JSON.stringify(CODEX_SELECTORS.shell)});
    const activeComposer = visibleNode(document, '.dream-composer') || visibleNode(document, ${JSON.stringify(CODEX_SELECTORS.composer)});
    const activeSidebar = visibleNode(document, '.dream-shell-sidebar') || visibleNode(document, ${JSON.stringify(CODEX_SELECTORS.sidebar)});
    const expectedPalette = ${JSON.stringify(expectedTheme?.palette ?? null)};
    const expectedMotion = ${JSON.stringify(expectedTheme?.motion?.kind ?? null)};
    const expectsFullscreenAsset = ${JSON.stringify(Boolean(expectedTheme?.modes?.fullscreen?.image))};
    const box = (node) => {
      if (!node) return null;
      const r = node.getBoundingClientRect();
      return { x: Math.round(r.x), y: Math.round(r.y), width: Math.round(r.width), height: Math.round(r.height) };
    };
    const home = visibleNode(document, '.dream-home');
    const suggestions = home?.querySelector('.group\\\\/home-suggestions') ?? null;
    const cards = suggestions ? [...suggestions.querySelectorAll('button')].map(box) : [];
    const visibleInViewport = (rect) => Boolean(rect) &&
      rect.width > 0 && rect.height > 0 &&
      rect.x < innerWidth && rect.y < innerHeight &&
      rect.x + rect.width > 0 && rect.y + rect.height > 0;
    const composerControls = [...(activeComposer?.querySelectorAll('button') ?? [])].map((button) => ({
      className: String(button.className || '').slice(0, 240),
      ariaLabel: String(button.getAttribute('aria-label') || '').slice(0, 120),
      title: String(button.getAttribute('title') || '').slice(0, 120),
      type: String(button.getAttribute('type') || '').slice(0, 40),
      disabled: Boolean(button.disabled),
      childTags: [...button.children].map((child) => child.tagName.toLowerCase()).slice(0, 8),
    }));
    const state = window.__CODEX_DREAM_SKIN_STATE__;
    const media = document.getElementById('codex-dream-skin-motion');
    const relayedMotion = state?.config?.motion?.transport === 'cdp';
    const motionLoaded = relayedMotion
      ? media?.tagName === 'CANVAS' && media.width > 0 && media.height > 0 && state.motionFrames >= 2 && Date.now() - state.lastMotionAt < 3000
      : expectedMotion === 'video'
      ? media?.tagName === 'VIDEO' && media.readyState >= 2 && !media.error && !media.paused && media.currentTime > 0
      : media?.tagName === 'IMG' && media.naturalWidth > 0;
    const expectsRailgun = state?.config?.controlProfile === 'railgun';
    const expectsHighVisibility = state?.config?.artVisibility === 'high';
    const expectsClearVisibility = state?.config?.artVisibility === 'clear';
    const stopControlPresent = composerControls.some((control) =>
      /(?:stop|cancel|abort|停止|取消|中止)/i.test(control.ariaLabel + ' ' + control.title));
    const rootStyle = getComputedStyle(document.documentElement);
    const colorSnapshot = (node) => {
      if (!node) return null;
      const style = getComputedStyle(node);
      return {
        backgroundColor: style.backgroundColor,
        color: style.color,
        borderColor: style.borderColor,
      };
    };
    const themeVariables = Object.fromEntries([
      '--dream-canvas', '--dream-surface', '--dream-surface-raised',
      '--dream-sidebar', '--dream-text', '--dream-text-muted', '--dream-line',
    ].map((property) => [property, rootStyle.getPropertyValue(property).trim()]));
    const palettePropertyMap = {
      canvas: '--dream-canvas',
      surface: '--dream-surface',
      surfaceRaised: '--dream-surface-raised',
      sidebar: '--dream-sidebar',
      text: '--dream-text',
      textMuted: '--dream-text-muted',
      line: '--dream-line',
    };
    const sameColorToken = (left, right) =>
      String(left ?? '').trim().toLowerCase() === String(right ?? '').trim().toLowerCase();
    const paletteMatchesExpected = !expectedPalette || Object.entries(expectedPalette).every(([key, value]) =>
      key === 'accent' || (
        sameColorToken(state?.config?.palette?.[key], value) &&
        sameColorToken(themeVariables[palettePropertyMap[key]], value)
      ));
    const result = {
      installed: document.documentElement.classList.contains('codex-dream-skin'),
      motionReady: !expectedMotion || (Boolean(motionLoaded) && document.documentElement.classList.contains('dream-motion-ready')),
      motionFrames: state?.motionFrames ?? 0,
      motionTransport: state?.config?.motion?.transport ?? null,
      version: window.__CODEX_DREAM_SKIN_STATE__?.version ?? null,
      expectedVersion: ${JSON.stringify(SKIN_VERSION)},
      stylePresent: Boolean(document.getElementById('codex-dream-skin-style')),
      chromePresent: Boolean(document.getElementById('codex-dream-skin-chrome')),
      chromePointerEvents: getComputedStyle(document.getElementById('codex-dream-skin-chrome') || document.body).pointerEvents,
      homePresent: Boolean(home),
      mainPresent: Boolean(activeShell),
      activeSurfaceStyled: Boolean(activeShell?.classList.contains('dream-shell-main')),
      home: box(home),
      suggestionsPresent: Boolean(suggestions),
      hero: box(home?.firstElementChild?.firstElementChild?.firstElementChild),
      cards,
      composer: box(activeComposer),
      composerControls,
      sidebar: box(activeSidebar),
      viewport: { width: innerWidth, height: innerHeight },
      documentOverflow: {
        x: document.documentElement.scrollWidth > document.documentElement.clientWidth,
        y: document.documentElement.scrollHeight > document.documentElement.clientHeight,
      },
      appearanceLight: document.documentElement.classList.contains('dream-theme-light'),
      artVisibilityHigh: document.documentElement.classList.contains('dream-art-visibility-high'),
      artVisibilityClear: document.documentElement.classList.contains('dream-art-visibility-clear'),
      railgunControls: document.documentElement.classList.contains('dream-controls-railgun'),
      fullscreenMode: document.documentElement.classList.contains('dream-mode-fullscreen'),
      fullscreenAssetAvailable: Boolean(state?.fullscreenArtUrl && state?.config?.fullscreen?.enabled),
      fullscreenAssetActive: state?.mode === 'fullscreen',
      lightningSendButton: Boolean(document.querySelector('.dream-composer button.dream-send-lightning, .composer-surface-chrome button.dream-send-lightning')),
      stopControlPresent,
      configuredPalette: state?.config?.palette ?? null,
      expectedPalette,
      paletteMatchesExpected,
      themeVariables,
      computedColors: {
        body: colorSnapshot(document.body),
        main: colorSnapshot(activeShell),
        sidebar: colorSnapshot(activeSidebar),
        composer: colorSnapshot(activeComposer),
      },
    };
    result.composerVisible = visibleInViewport(result.composer);
    result.cardsVisible = result.cards.every(visibleInViewport);
    result.checks = {
      installed: result.installed,
      version: result.version === result.expectedVersion,
      style: result.stylePresent,
      chrome: result.chromePresent && result.chromePointerEvents === 'none',
      composer: !result.composer || result.composerVisible,
      main: result.mainPresent,
      surface: result.activeSurfaceStyled,
      shell: result.fullscreenMode || Boolean(result.sidebar),
      fullscreen: !expectsFullscreenAsset || !result.fullscreenMode ||
        (result.fullscreenAssetAvailable && result.fullscreenAssetActive),
      motion: result.motionReady,
      controls: !expectsRailgun || !activeComposer || (result.railgunControls &&
        (result.lightningSendButton || result.stopControlPresent)),
      highVisibility: !expectsHighVisibility || result.artVisibilityHigh,
      clearVisibility: !expectsClearVisibility || result.artVisibilityClear,
      palette: result.paletteMatchesExpected,
      home: !result.homePresent || (Boolean(result.home) && visibleInViewport(result.home)),
    };
    result.pass = Object.values(result.checks).every(Boolean);
    return result;
  })()`;
  return session.evaluate(inCodexWindow(expression));
}

async function waitForVerifiedSession(session, timeoutMs, expectedTheme = null) {
  const deadline = Date.now() + timeoutMs;
  let lastResult;
  let lastError;
  while (Date.now() < deadline) {
    try {
      lastResult = await verifySession(session, expectedTheme);
      lastError = null;
      if (lastResult.pass) return lastResult;
    } catch (error) {
      lastError = error;
    }
    await new Promise((resolve) => setTimeout(resolve, 500));
  }
  if (!lastResult && lastError) throw lastError;
  return lastResult;
}

async function capture(session, outputPath) {
  await fs.mkdir(path.dirname(outputPath), { recursive: true });
  const result = await session.send("Page.captureScreenshot", {
    format: "png",
    fromSurface: true,
    captureBeyondViewport: false,
  });
  await fs.writeFile(outputPath, Buffer.from(result.data, "base64"));
}

async function runOneShot(options) {
  const connected = await connectCodexTargets(options.port, options.timeoutMs, options.browserId);
  const loadedPayload = (options.mode === "once" || options.reload)
    ? await loadPayload(options.themeDir) : null;
  const expectedTheme = loadedPayload?.theme ??
    (options.mode === "verify" ? (await loadTheme(options.themeDir)).theme : null);
  const payload = loadedPayload?.payload ?? null;
  const results = [];
  const relay = new MotionRelay(async (frame, revision) => {
    const expression = inCodexWindow(`window.__CODEX_DREAM_SKIN_STATE__?.acceptMotionFrame?.(${JSON.stringify(frame)}, ${JSON.stringify(revision)}) ?? false`);
    await Promise.all(connected.map(({session}) => session.evaluate(expression).catch(() => false)));
  });
  if (loadedPayload?.motionRelay && options.mode === "once") relay.setSource(loadedPayload.motionRelay);
  let screenshotCaptured = false;
  try {
    for (const { target, session, probe } of connected) {
      try {
        if (options.mode === "remove") await removeFromSession(session);
        else if (options.mode === "once") await applyToSession(session, payload);
        if (options.mode === "once") {
          await new Promise((resolve) => setTimeout(resolve, 850));
        }
        if (options.reload) {
          await session.send("Page.reload", { ignoreCache: true });
          await new Promise((resolve) => setTimeout(resolve, 1600));
          if (options.mode !== "remove") await applyToSession(session, payload);
        }
        const verified = options.mode === "remove"
          ? await verifyRemovedSession(session)
          : (options.reload || options.mode === "once" || options.mode === "verify")
            ? await waitForVerifiedSession(session, options.timeoutMs, expectedTheme)
            : await verifySession(session, expectedTheme);
        results.push({ targetId: target.id, markers: probe.markers, result: verified });
        if (options.screenshot && !screenshotCaptured) {
          await capture(session, options.screenshot);
          screenshotCaptured = true;
        }
      } finally {
        session.close();
      }
    }
  } finally {
    relay.stop();
    for (const { session } of connected) session.close();
  }
  console.log(JSON.stringify({ mode: options.mode, port: options.port, targets: results }, null, 2));
  const failed = results.length === 0 || results.some((item) =>
    options.mode === "remove" ? item.result !== true : !item.result?.pass);
  if (failed) process.exitCode = 2;
}

async function runWatch(options) {
  const identityAnchor = await connectBrowserIdentityAnchor(options.port, options.browserId);
  const sessions = new Map();
  const earlyScripts = new Map();
  const fallbackTargets = new Map();
  const fallbackListeners = new Set();
  const targetFailures = new Map();
  const healthCheckedAt = new Map();
  let stopping = false;
  let listFailures = 0;
  let lastListErrorLogAt = 0;
  let lastThemeErrorLogAt = 0;
  let lastStrongThemeAuditAt = 0;
  let loadedPayload = null;
  let paused = false;
  const relay = new MotionRelay(async (frame, revision) => {
    if (stopping || paused) return;
    const expression = inCodexWindow(`window.__CODEX_DREAM_SKIN_STATE__?.acceptMotionFrame?.(${JSON.stringify(frame)}, ${JSON.stringify(revision)}) ?? false`);
    await Promise.all([...sessions.values()].map(session => session.evaluate(expression).catch(() => false)));
  });
  const stop = () => { stopping = true; };
  const rejectTarget = (target, baseDelayMs, error = null) => {
    const previous = targetFailures.get(target.id) ?? { failures: 0, lastLogAt: 0 };
    const failures = previous.failures + 1;
    const delayMs = Math.min(30000, baseDelayMs * (2 ** Math.min(failures - 1, 4)));
    const now = Date.now();
    if (error && (failures === 1 || now - previous.lastLogAt >= 30000)) {
      console.error(`[dream-skin] inject failed for ${target.id}: ${error.message}; retrying in ${delayMs}ms`);
      previous.lastLogAt = now;
    }
    targetFailures.set(target.id, { failures, lastLogAt: previous.lastLogAt, until: now + delayMs });
  };
  const attachLoadFallback = (id, target, session) => {
    if (fallbackListeners.has(id)) return;
    fallbackListeners.add(id);
    let lastReinjectErrorLogAt = 0;
    session.on("Page.loadEventFired", () => {
      if (!fallbackTargets.get(id)) return;
      setTimeout(() => {
        const operation = paused ? removeFromSession(session) : applyToSession(session, loadedPayload.payload);
        operation.catch((error) => {
          if (Date.now() - lastReinjectErrorLogAt >= 30000) {
            console.error(`[dream-skin] reinject failed for ${target.id}: ${error.message}`);
            lastReinjectErrorLogAt = Date.now();
          }
        });
      }, 250);
    });
  };
  process.on("SIGINT", stop);
  process.on("SIGTERM", stop);

  try {
    loadedPayload = await loadPayload(options.themeDir);
    lastStrongThemeAuditAt = Date.now();
    paused = await fileExists(options.pauseFile);
    while (!stopping) {
      if (identityAnchor.closed) {
        console.error("[dream-skin] original CDP browser identity closed; watcher is stopping instead of reconnecting");
        process.exitCode = 3;
        break;
      }
      let targets = [];
      try {
        targets = await listAppTargets(options.port);
        listFailures = 0;
      } catch (error) {
        listFailures += 1;
        const retryMs = Math.min(10000, 1000 * (2 ** Math.min(listFailures - 1, 4)));
        if (listFailures === 1 || Date.now() - lastListErrorLogAt >= 30000) {
          console.error(`[dream-skin] ${new Date().toISOString()} ${error.message}; retrying in ${retryMs}ms`);
          lastListErrorLogAt = Date.now();
        }
        await new Promise((resolve) => setTimeout(resolve, retryMs));
        continue;
      }

      const nextPaused = await fileExists(options.pauseFile);
      let nextPayload = loadedPayload;
      if (!nextPaused) {
        try {
          const now = Date.now();
          let shouldAudit = !loadedPayload || now - lastStrongThemeAuditAt >= STRONG_THEME_AUDIT_MS;
          if (!shouldAudit) {
            try {
              shouldAudit = await readThemeSourceStamp(loadedPayload) !== loadedPayload.sourceStamp;
            } catch {
              shouldAudit = true;
            }
          }
          if (shouldAudit) {
            const candidateTheme = await loadTheme(options.themeDir);
            lastStrongThemeAuditAt = now;
            if (!loadedPayload || candidateTheme.fingerprint !== loadedPayload.fingerprint) {
              nextPayload = await loadPayload(options.themeDir, candidateTheme);
            } else {
              loadedPayload.sourceStamp = candidateTheme.sourceStamp;
            }
          }
        } catch (error) {
          if (Date.now() - lastThemeErrorLogAt >= 30000) {
            console.error(`[dream-skin] theme update rejected: ${error.message}; keeping the active theme`);
            lastThemeErrorLogAt = Date.now();
          }
        }
      }
      const pauseChanged = nextPaused !== paused;
      const payloadChanged = !nextPaused && nextPayload !== loadedPayload;
      loadedPayload = nextPayload;
      paused = nextPaused;
      relay.setSource(paused ? null : loadedPayload?.motionRelay);

      if (pauseChanged || payloadChanged) {
        for (const [id, session] of sessions) {
          try {
            const previousEarlyScript = earlyScripts.get(id);
            if (paused) {
              await removeFromSession(session);
              await removeEarlyPayload(session, previousEarlyScript);
              earlyScripts.delete(id);
              fallbackTargets.delete(id);
              fallbackListeners.delete(id);
            } else {
              let nextEarlyScript = null;
              try {
                nextEarlyScript = await registerEarlyPayload(
                  session,
                  loadedPayload.payload,
                  loadedPayload.fingerprint,
                );
                if (!nextEarlyScript) throw new Error("CDP did not return an early-script identifier");
                fallbackTargets.set(id, false);
              } catch (error) {
                fallbackTargets.set(id, true);
                console.error(`[dream-skin] early theme refresh unavailable for ${id}: ${error.message}`);
                attachLoadFallback(id, { id }, session);
              }
              if (nextEarlyScript) earlyScripts.set(id, nextEarlyScript);
              else earlyScripts.delete(id);
              await removeEarlyPayload(session, previousEarlyScript);
              await applyToSession(session, loadedPayload.payload);
            }
          } catch (error) {
            console.error(`[dream-skin] live theme update failed for ${id}: ${error.message}`);
            await removeEarlyPayload(session, earlyScripts.get(id));
            earlyScripts.delete(id);
            fallbackTargets.delete(id);
            fallbackListeners.delete(id);
            session.close();
            sessions.delete(id);
          }
        }
        console.log(paused ? "[dream-skin] paused" : `[dream-skin] active theme ${loadedPayload.theme.id}`);
      }

      const activeIds = new Set(targets.map((target) => target.id));
      for (const id of targetFailures.keys()) {
        if (!activeIds.has(id)) targetFailures.delete(id);
      }
      for (const [id, session] of sessions) {
        if (!activeIds.has(id) || session.closed) {
          await removeEarlyPayload(session, earlyScripts.get(id));
          earlyScripts.delete(id);
          fallbackTargets.delete(id);
          fallbackListeners.delete(id);
          session.close();
          sessions.delete(id);
          targetFailures.delete(id);
          healthCheckedAt.delete(id);
        } else if (!paused && Date.now() - (healthCheckedAt.get(id) ?? 0) >= 3000) {
          healthCheckedAt.set(id, Date.now());
          try {
            const healthy = await session.evaluate(inCodexWindow(rendererHealthExpression()));
            if (!healthy) {
              await applyToSession(session, loadedPayload.payload);
              console.log(`[dream-skin] recovered renderer ${id}`);
            }
          } catch {
            // A document can briefly lack shell markers during navigation.
            // Keep its early script and recheck after the route mounts.
          }
        }
      }

      for (const target of targets) {
        if (identityAnchor.closed) break;
        if (sessions.has(target.id)) continue;
        if ((targetFailures.get(target.id)?.until ?? 0) > Date.now()) continue;
        let session;
        let earlyScriptId = null;
        try {
          session = await connectTarget(target, options.port);
          if (identityAnchor.closed) throw new CdpIdentityMismatchError("Original CDP browser identity closed");
          let earlyInjectionFallback = false;
          if (!paused) {
            try {
              earlyScriptId = await registerEarlyPayload(
                session,
                loadedPayload.payload,
                loadedPayload.fingerprint,
              );
              if (!earlyScriptId) throw new Error("CDP did not return an early-script identifier");
              await session.evaluate(earlyPayloadFor(loadedPayload.payload, loadedPayload.fingerprint));
            } catch (error) {
              await removeEarlyPayload(session, earlyScriptId);
              earlyScriptId = null;
              earlyInjectionFallback = true;
              console.error(`[dream-skin] early injection unavailable for ${target.id}: ${error.message}`);
            }
          }
          const probe = await waitForCodexProbe(session);
          if (!probe?.codex) {
            await removeEarlyPayload(session, earlyScriptId);
            rejectTarget(target, 5000);
            session.close();
            continue;
          }
          fallbackTargets.set(target.id, earlyInjectionFallback);
          if (earlyInjectionFallback) attachLoadFallback(target.id, target, session);
          if (identityAnchor.closed) throw new CdpIdentityMismatchError("Original CDP browser identity closed");
          let earlyApplied = false;
          if (!paused && !earlyInjectionFallback) {
            earlyApplied = await session.evaluate(
              `window.__CODEX_DREAM_SKIN_EARLY_APPLIED__ === ${JSON.stringify(loadedPayload.fingerprint)}`,
            ).catch(() => false);
          }
          if (paused) await removeFromSession(session);
          else if (!earlyApplied) await applyToSession(session, loadedPayload.payload);
          sessions.set(target.id, session);
          if (earlyScriptId) earlyScripts.set(target.id, earlyScriptId);
          targetFailures.delete(target.id);
          console.log(`[dream-skin] injected target ${target.id}`);
        } catch (error) {
          await removeEarlyPayload(session, earlyScriptId);
          fallbackTargets.delete(target.id);
          fallbackListeners.delete(target.id);
          session?.close();
          if (identityAnchor.closed || error instanceof CdpIdentityMismatchError) break;
          rejectTarget(target, 2500, error);
        }
      }
      await new Promise((resolve) => setTimeout(resolve, 1200));
    }
  } finally {
    relay.stop();
    identityAnchor.close();
    for (const [id, session] of sessions) {
      await removeEarlyPayload(session, earlyScripts.get(id));
      session.close();
    }
    earlyScripts.clear();
    fallbackTargets.clear();
    fallbackListeners.clear();
    healthCheckedAt.clear();
  }
}

if (path.resolve(process.argv[1] || "") === path.resolve(scriptPath)) {
  const options = parseArgs(process.argv.slice(2));
  if (options.mode === "self-test") {
  const valid = validatedDebuggerUrl({ webSocketDebuggerUrl: `ws://127.0.0.1:${options.port}/devtools/page/test` }, options.port);
  const browserId = browserIdFromVersion({
    webSocketDebuggerUrl: `ws://127.0.0.1:${options.port}/devtools/browser/test-browser`,
  }, options.port);
  const invalid = [
    "ws://example.com/devtools/page/test",
    `ws://127.0.0.1:${options.port + 1}/devtools/page/test`,
    `wss://127.0.0.1:${options.port}/devtools/page/test`,
    `ws://user@127.0.0.1:${options.port}/devtools/page/test`,
    `ws://127.0.0.1:${options.port}/unexpected/test`,
    `ws://127.0.0.1:${options.port}/devtools/page/test?query=1`,
  ];
  for (const value of invalid) {
    let rejected = false;
    try { validatedDebuggerUrl({ webSocketDebuggerUrl: value }, options.port); } catch { rejected = true; }
    if (!rejected) throw new Error(`CDP URL validation accepted an unsafe URL: ${value}`);
  }
  const invalidBrowserUrls = [
    `ws://127.0.0.1:${options.port}/devtools/page/not-a-browser`,
    `ws://127.0.0.1:${options.port}/devtools/browser/bad%20id`,
    `ws://127.0.0.1:${options.port}/devtools/browser/test?query=1`,
  ];
  for (const value of invalidBrowserUrls) {
    let rejected = false;
    try { browserIdFromVersion({ webSocketDebuggerUrl: value }, options.port); } catch { rejected = true; }
    if (!rejected) throw new Error(`Browser identity validation accepted an unsafe URL: ${value}`);
  }
  const validPageTarget = {
    id: "page-test",
    type: "page",
    url: "app://codex/",
    webSocketDebuggerUrl: `ws://127.0.0.1:${options.port}/devtools/page/page-test`,
  };
  const invalidPageTargets = [
    { ...validPageTarget, webSocketDebuggerUrl: `ws://127.0.0.1:${options.port}/devtools/browser/page-test` },
    { ...validPageTarget, id: "other-page" },
    { ...validPageTarget, id: 123 },
    { ...validPageTarget, type: "other" },
  ];
  const validWebviewTarget = {
    ...validPageTarget,
    id: "webview-test",
    type: "webview",
    url: "https://chatgpt.com/codex",
    webSocketDebuggerUrl: `ws://127.0.0.1:${options.port}/devtools/page/webview-test`,
  };
  const untrustedWebviewTarget = {
    ...validWebviewTarget,
    id: "evil-test",
    url: "https://example.com/",
    webSocketDebuggerUrl: `ws://127.0.0.1:${options.port}/devtools/page/evil-test`,
  };
  const validIframeTarget = {
    ...validPageTarget,
    id: "iframe-test",
    type: "iframe",
    webSocketDebuggerUrl: `ws://127.0.0.1:${options.port}/devtools/page/iframe-test`,
  };
  if (!valid || browserId !== "test-browser" || !isValidCdpRendererTarget(validPageTarget, options.port) ||
      !isValidCdpRendererTarget(validWebviewTarget, options.port) ||
      !isValidCdpRendererTarget(validIframeTarget, options.port) ||
      isValidCdpRendererTarget(untrustedWebviewTarget, options.port) ||
      invalidPageTargets.some((item) => isValidCdpRendererTarget(item, options.port))) {
    throw new Error("CDP URL and target validation self-test failed");
  }
  const validMessage = parseCdpMessage('{"id":7,"result":{"ok":true}}');
  const invalidMessages = ["{not-json", "null", '"text"', "42", "true"];
  if (validMessage?.id !== 7 || validMessage.result?.ok !== true ||
      invalidMessages.some((value) => parseCdpMessage(value) !== null)) {
    throw new Error("CDP message validation self-test failed");
  }
  if (/dispatchKeyEvent|dispatchMouseEvent/.test(capture.toString())) {
    throw new Error("Screenshot capture must not dispatch renderer input events");
  }
  console.log(JSON.stringify({ pass: true, version: SKIN_VERSION, test: "loopback-cdp-validation" }));
  } else if (options.mode === "check-payload") {
    const loaded = await loadPayload(options.themeDir);
    const unresolved = ["__DREAM_CSS_JSON__", "__DREAM_ART_JSON__", "__DREAM_FULLSCREEN_ART_JSON__", "__DREAM_THEME_JSON__"]
      .some((placeholder) => loaded.payload.includes(placeholder));
    if (unresolved) {
      throw new Error("Payload placeholders were not fully replaced");
    }
    console.log(JSON.stringify({
      pass: true,
      version: SKIN_VERSION,
      payloadBytes: Buffer.byteLength(loaded.payload),
      themeId: loaded.theme.id,
      appearance: loaded.theme.appearance,
      art: loaded.theme.art,
      artMetadata: loaded.theme.artMetadata ?? null,
      fullscreen: loaded.theme.modes?.fullscreen ?? null,
    }));
  } else if (options.mode === "watch") await runWatch(options);
  else await runOneShot(options);
}
