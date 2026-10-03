((cssText, artDataUrl, fullscreenArtDataUrl, rawConfig) => {
  const STATE_KEY = "__CODEX_DREAM_SKIN_STATE__";
  const STYLE_ID = "codex-dream-skin-style";
  const CHROME_ID = "codex-dream-skin-chrome";
  const MOTION_ID = "codex-dream-skin-motion";
  const ROOT_CLASSES = [
    "codex-dream-skin",
    "dream-theme-light",
    "dream-theme-dark",
    "dream-art-wide",
    "dream-art-standard",
    "dream-focus-left",
    "dream-focus-center",
    "dream-focus-right",
    "dream-safe-left",
    "dream-safe-center",
    "dream-safe-right",
    "dream-safe-none",
    "dream-task-ambient",
    "dream-task-banner",
    "dream-task-off",
    "dream-art-visibility-high",
    "dream-art-visibility-clear",
    "dream-mode-fullscreen",
    "dream-controls-railgun",
    "dream-motion-ready",
  ];
  const ROOT_PROPERTIES = [
    "--dream-art",
    "--dream-art-position",
    "--dream-focus-x",
    "--dream-focus-y",
    "--dream-accent",
    "--dream-accent-ink",
    "--dream-image-luma",
    "--dream-canvas",
    "--dream-surface",
    "--dream-surface-raised",
    "--dream-sidebar",
    "--dream-text",
    "--dream-text-muted",
    "--dream-line",
  ];
  const HOME_UTILITY_CLASS = "dream-home-utility";
  const SEND_LIGHTNING_CLASS = "dream-send-lightning";
  const SHELL_MAIN_CLASS = "dream-shell-main";
  const SHELL_SIDEBAR_CLASS = "dream-shell-sidebar";
  const COMPOSER_CLASS = "dream-composer";
  const SHELL_SELECTOR = "main.main-surface, main[data-app-shell-main-surface], [data-app-shell-main-surface]";
  const compatibleCssText = cssText
    .replaceAll("main.main-surface", `:is(main.main-surface, .${SHELL_MAIN_CLASS})`)
    .replaceAll("aside.app-shell-left-panel", `:is(aside.app-shell-left-panel, .${SHELL_SIDEBAR_CLASS})`)
    .replaceAll(".composer-surface-chrome", `:is(.composer-surface-chrome, .${COMPOSER_CLASS})`);
  const installToken = {};
  let samplingNativeShell = false;
  let observer = null;
  window.__CODEX_DREAM_SKIN_DISABLED__ = false;

  const clamp = (value, min = 0, max = 1) => Math.min(max, Math.max(min, Number(value)));
  const luminance = (red, green, blue) => {
    const linear = [red, green, blue].map((value) => {
      const channel = value / 255;
      return channel <= .04045 ? channel / 12.92 : ((channel + .055) / 1.055) ** 2.4;
    });
    return .2126 * linear[0] + .7152 * linear[1] + .0722 * linear[2];
  };
  const defaultProfile = {
    appearance: "dark",
    accent: [108, 131, 142],
    focusX: .5,
    focusY: .5,
    aspect: 1.6,
    luma: .32,
    safeArea: "center",
  };

  const normalizeConfig = (value) => {
    const config = value && typeof value === "object" ? value : {};
    const art = config.art && typeof config.art === "object" ? config.art : {};
    const hasNumber = (candidate) =>
      (typeof candidate === "number" || (typeof candidate === "string" && candidate.trim() !== "")) &&
      Number.isFinite(Number(candidate));
    const colorPattern = /^(?:#[\da-f]{3,8}|(?:rgb|hsl|oklch|oklab)\([^;{}]{1,96}\))$/i;
    const safeColor = (candidate) => {
      const requested = typeof candidate === "string" ? candidate.trim() : "";
      return colorPattern.test(requested) ? requested : null;
    };
    const palette = config.palette && typeof config.palette === "object" ? config.palette : {};
    const controls = config.controls && typeof config.controls === "object" ? config.controls : {};
    const ui = config.ui && typeof config.ui === "object" ? config.ui : {};
    const modes = config.modes && typeof config.modes === "object" ? config.modes : {};
    const fullscreen = modes.fullscreen && typeof modes.fullscreen === "object" ? modes.fullscreen : {};
    const fullscreenArt = fullscreen.art && typeof fullscreen.art === "object" ? fullscreen.art : {};
    const appearance = ["auto", "light", "dark"].includes(config.appearance)
      ? config.appearance
      : "auto";
    const safeArea = ["auto", "left", "right", "center", "none"].includes(art.safeArea)
      ? art.safeArea
      : "auto";
    const taskMode = ["auto", "ambient", "banner", "off"].includes(art.taskMode)
      ? art.taskMode
      : "auto";
    const metadataRatio = Number(config?.artMetadata?.ratio);
    return {
      appearance,
      safeArea,
      taskMode,
      focusX: hasNumber(art.focusX) ? clamp(art.focusX) : null,
      focusY: hasNumber(art.focusY) ? clamp(art.focusY) : null,
      accent: safeColor(palette.accent),
      palette: {
        canvas: safeColor(palette.canvas),
        surface: safeColor(palette.surface),
        surfaceRaised: safeColor(palette.surfaceRaised),
        sidebar: safeColor(palette.sidebar),
        text: safeColor(palette.text),
        textMuted: safeColor(palette.textMuted),
        line: safeColor(palette.line),
      },
      controlProfile: controls.profile === "railgun" ? "railgun" : "standard",
      artVisibility: ["high", "clear"].includes(ui.artVisibility) ? ui.artVisibility : "normal",
      fullscreen: {
        enabled: Boolean(fullscreen.image && fullscreenArtDataUrl),
        focusX: hasNumber(fullscreenArt.focusX) ? clamp(fullscreenArt.focusX) : null,
        focusY: hasNumber(fullscreenArt.focusY) ? clamp(fullscreenArt.focusY) : null,
        aspect: Number.isFinite(Number(fullscreen?.artMetadata?.ratio))
          ? Number(fullscreen.artMetadata.ratio) : null,
      },
      initialAspect: Number.isFinite(metadataRatio) && metadataRatio > 0 ? metadataRatio : null,
      motion: config.motion && ["video", "scene", "web"].includes(config.motion.kind) &&
        (config.motion.transport === "cdp" || /^[a-f0-9]{64}$/.test(config.motion.token || ""))
        ? { kind: config.motion.kind, revision: /^[a-f0-9]{16}$/.test(config.motion.revision || "") ? config.motion.revision : "0", token: config.motion.token, transport: config.motion.transport === "cdp" ? "cdp" : "http" }
        : null,
    };
  };

  const releaseMotion = (element) => {
    if (!element) return;
    element.__dreamDisposed = true;
    if (element.__dreamDecoder) { element.__dreamDecoder.onload = null; element.__dreamDecoder.onerror = null; }
    if (element.__dreamRetry) clearTimeout(element.__dreamRetry);
    if (element.__dreamFrameUrl) URL.revokeObjectURL(element.__dreamFrameUrl);
    if (element.__dreamPreviousFrameUrl) URL.revokeObjectURL(element.__dreamPreviousFrameUrl);
    element.pause?.();
    element.removeAttribute?.("src");
    element.load?.();
    element.remove();
  };

  const previous = window[STATE_KEY];
  if (previous?.observer) previous.observer.disconnect();
  if (previous?.timer) clearInterval(previous.timer);
  if (previous?.scheduler?.timeout) clearTimeout(previous.scheduler.timeout);
  if (previous?.artUrl) URL.revokeObjectURL(previous.artUrl);
  if (previous?.fullscreenArtUrl) URL.revokeObjectURL(previous.fullscreenArtUrl);
  releaseMotion(document.getElementById(MOTION_ID));
  const createArtUrl = (dataUrl) => {
    if (!dataUrl) return null;
    const comma = dataUrl.indexOf(",");
    const binary = atob(dataUrl.slice(comma + 1));
    const bytes = new Uint8Array(binary.length);
    for (let index = 0; index < binary.length; index += 1) bytes[index] = binary.charCodeAt(index);
    const mime = /^data:([^;,]+)/.exec(dataUrl)?.[1] || "image/png";
    return URL.createObjectURL(new Blob([bytes], { type: mime }));
  };
  const artUrl = createArtUrl(artDataUrl);
  const fullscreenArtUrl = createArtUrl(fullscreenArtDataUrl);
  const config = normalizeConfig(rawConfig);
  let profile = {
    ...defaultProfile,
    aspect: config.initialAspect ?? defaultProfile.aspect,
  };
  const existingStyle = document.getElementById(STYLE_ID);
  if (existingStyle) {
    existingStyle.textContent = compatibleCssText;
    existingStyle.dataset.dreamVersion = "4";
  }

  const analyzeArt = () => new Promise((resolve) => {
    if (typeof Image !== "function") {
      resolve(defaultProfile);
      return;
    }
    const image = new Image();
    image.onload = () => {
      try {
        const width = 48;
        const height = Math.max(12, Math.round(width * image.naturalHeight / image.naturalWidth));
        const canvas = document.createElement("canvas");
        canvas.width = width;
        canvas.height = height;
        const context = canvas.getContext?.("2d", { willReadFrequently: true });
        if (!context) throw new Error("Canvas is unavailable");
        context.drawImage(image, 0, 0, width, height);
        const pixels = context.getImageData(0, 0, width, height).data;
        let count = 0;
        let totalRed = 0;
        let totalGreen = 0;
        let totalBlue = 0;
        let totalBrightness = 0;
        const samples = [];
        const sampleMap = new Array(width * height);
        for (let offset = 0; offset < pixels.length; offset += 4) {
          if (pixels[offset + 3] < 96) continue;
          const red = pixels[offset];
          const green = pixels[offset + 1];
          const blue = pixels[offset + 2];
          const light = (.2126 * red + .7152 * green + .0722 * blue) / 255;
          const sample = { red, green, blue, light, index: offset / 4 };
          samples.push(sample);
          sampleMap[sample.index] = sample;
          totalRed += red;
          totalGreen += green;
          totalBlue += blue;
          totalBrightness += light;
          count += 1;
        }
        if (!count) throw new Error("Image contains no opaque pixels");
        const average = [totalRed / count, totalGreen / count, totalBlue / count];
        const averageBrightness = totalBrightness / count;
        const information = (start, end) => {
          let total = 0;
          let totalSquared = 0;
          let edges = 0;
          let edgeCount = 0;
          let sampleCount = 0;
          for (let y = 0; y < height; y += 1) {
            for (let x = start; x < end; x += 1) {
              const sample = sampleMap[y * width + x];
              if (!sample) continue;
              total += sample.light;
              totalSquared += sample.light * sample.light;
              sampleCount += 1;
              const previousSample = x > start ? sampleMap[y * width + x - 1] : null;
              const above = y > 0 ? sampleMap[(y - 1) * width + x] : null;
              if (previousSample) { edges += Math.abs(sample.light - previousSample.light); edgeCount += 1; }
              if (above) { edges += Math.abs(sample.light - above.light); edgeCount += 1; }
            }
          }
          const mean = sampleCount ? total / sampleCount : 0;
          const variance = sampleCount ? Math.max(0, totalSquared / sampleCount - mean * mean) : 1;
          return Math.sqrt(variance) * .58 + (edgeCount ? edges / edgeCount : 1) * .42;
        };
        const zoneWidth = Math.max(1, Math.floor(width * .38));
        const leftInformation = information(0, zoneWidth);
        const rightInformation = information(width - zoneWidth, width);
        let safeArea = "center";
        if (leftInformation < rightInformation * .86) safeArea = "left";
        else if (rightInformation < leftInformation * .86) safeArea = "right";
        let focusWeight = 0;
        let focusX = 0;
        let focusY = 0;
        let accentWeight = 0;
        let accent = [0, 0, 0];
        for (const sample of samples) {
          const x = sample.index % width;
          const y = Math.floor(sample.index / width);
          const difference = Math.sqrt(
            (sample.red - average[0]) ** 2 +
            (sample.green - average[1]) ** 2 +
            (sample.blue - average[2]) ** 2,
          ) / 441.7;
          const saliency = .03 + difference ** 1.35;
          focusX += (x / Math.max(1, width - 1)) * saliency;
          focusY += (y / Math.max(1, height - 1)) * saliency;
          focusWeight += saliency;
          const max = Math.max(sample.red, sample.green, sample.blue);
          const min = Math.min(sample.red, sample.green, sample.blue);
          const saturation = max ? (max - min) / max : 0;
          const usableLight = 1 - Math.min(1, Math.abs(sample.light - .46) / .54);
          const weight = saturation ** 2 * (.15 + usableLight);
          accent[0] += sample.red * weight;
          accent[1] += sample.green * weight;
          accent[2] += sample.blue * weight;
          accentWeight += weight;
        }
        const resolvedAccent = accentWeight > 1
          ? accent.map((channel) => Math.round(channel / accentWeight))
          : average.map((channel) => Math.round(channel));
        let resolvedFocusX = clamp(focusX / focusWeight);
        if (safeArea === "left") resolvedFocusX = Math.max(.64, resolvedFocusX);
        if (safeArea === "right") resolvedFocusX = Math.min(.36, resolvedFocusX);
        resolve({
          appearance: averageBrightness >= .58 ? "light" : "dark",
          accent: resolvedAccent,
          focusX: resolvedFocusX,
          focusY: clamp(focusY / focusWeight),
          aspect: image.naturalWidth / Math.max(1, image.naturalHeight),
          luma: clamp(averageBrightness),
          safeArea,
        });
      } catch {
        resolve(defaultProfile);
      }
    };
    image.onerror = () => resolve(defaultProfile);
    image.src = artUrl;
  });

  const detectShellAppearance = () => {
    const root = document.documentElement;
    const body = document.body;
    const classes = `${root?.className || ""} ${body?.className || ""}`
      .toLowerCase()
      .replace(/\bdream-theme-(?:dark|light)\b/g, "");
    if (/\b(dark|electron-dark|theme-dark|appearance-dark)\b/.test(classes)) return "dark";
    if (/\b(light|electron-light|theme-light|appearance-light)\b/.test(classes)) return "light";

    const dataTheme = (
      root?.getAttribute?.("data-theme") ||
      root?.getAttribute?.("data-appearance") ||
      root?.getAttribute?.("data-color-mode") ||
      body?.getAttribute?.("data-theme") ||
      body?.getAttribute?.("data-appearance") ||
      ""
    ).toLowerCase();
    if (dataTheme.includes("dark")) return "dark";
    if (dataTheme.includes("light")) return "light";

    try {
      const hadSkin = root?.classList?.contains?.("codex-dream-skin");
      const savedSkinClasses = hadSkin
        ? ROOT_CLASSES.filter((className) => root.classList.contains(className))
        : [];
      samplingNativeShell = true;
      if (hadSkin) root.classList.remove(...ROOT_CLASSES);
      try {
        const colorScheme = getComputedStyle(root).colorScheme || "";
        if (colorScheme.includes("dark") && !colorScheme.includes("light")) return "dark";
        if (colorScheme.includes("light") && !colorScheme.includes("dark")) return "light";
      } finally {
        if (hadSkin) root.classList.add(...savedSkinClasses);
        observer?.takeRecords?.();
        samplingNativeShell = false;
      }
    } catch {
      samplingNativeShell = false;
    }
    try {
      return window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
    } catch {}
    return "light";
  };

  const clearSkinDom = () => {
    const root = document.documentElement;
    root?.classList.remove(...ROOT_CLASSES);
    for (const property of ROOT_PROPERTIES) root?.style.removeProperty(property);
    document.querySelectorAll(".dream-home").forEach((node) => {
      node.classList.remove("dream-home", "dream-home-legacy");
    });
    document.querySelectorAll(".dream-task").forEach((node) => node.classList.remove("dream-task"));
    document.querySelectorAll(".dream-home-shell").forEach((node) => node.classList.remove("dream-home-shell"));
    document.querySelectorAll(`.${HOME_UTILITY_CLASS}`).forEach((node) => node.classList.remove(HOME_UTILITY_CLASS));
    document.querySelectorAll(`.${SEND_LIGHTNING_CLASS}`).forEach((node) => node.classList.remove(SEND_LIGHTNING_CLASS));
    document.querySelectorAll(`.${SHELL_MAIN_CLASS}`).forEach((node) => node.classList.remove(SHELL_MAIN_CLASS));
    document.querySelectorAll(`.${SHELL_SIDEBAR_CLASS}`).forEach((node) => node.classList.remove(SHELL_SIDEBAR_CLASS));
    document.querySelectorAll(`.${COMPOSER_CLASS}`).forEach((node) => node.classList.remove(COMPOSER_CLASS));
    document.getElementById(STYLE_ID)?.remove();
    document.getElementById(CHROME_ID)?.remove();
    releaseMotion(document.getElementById(MOTION_ID));
  };

  const ensureMotion = (root) => {
    const motion = config.motion;
    let element = document.getElementById(MOTION_ID);
    if (!motion || !document.body) {
      releaseMotion(element);
      root.classList.remove("dream-motion-ready");
      return;
    }
    const video = motion.kind === "video" && motion.transport !== "cdp";
    const tag = motion.transport === "cdp" ? "CANVAS" : video ? "VIDEO" : "IMG";
    if (element && element.tagName !== tag) { releaseMotion(element); element = null; }
    if (!element) {
      element = document.createElement(tag.toLowerCase());
      element.id = MOTION_ID;
      element.className = "dream-motion-media";
      element.setAttribute("aria-hidden", "true");
      element.style.pointerEvents = "none";
      const current = () => !element.__dreamDisposed && document.getElementById(MOTION_ID) === element && !window.__CODEX_DREAM_SKIN_DISABLED__;
      const ready = () => {
        if (!current()) return;
        element.dataset.motionState = "ready";
        root.classList.add("dream-motion-ready");
        if (motion.transport === "cdp") {
          element.__dreamFrameBusy = false;
          if (element.__dreamPreviousFrameUrl) URL.revokeObjectURL(element.__dreamPreviousFrameUrl);
          element.__dreamPreviousFrameUrl = null;
          const state = window[STATE_KEY];
          if (state?.installToken === installToken) {
            state.motionFrames = (state.motionFrames || 0) + 1;
            state.lastMotionAt = Date.now();
          }
        }
      };
      element.__dreamMarkReady = ready;
      let attempts = 0;
      const reload = () => {
        if (!current()) return;
        element.dataset.motionState = "loading";
        element.src = `http://127.0.0.1:47866/${video ? "video" : "scene"}?t=${motion.token}&v=${motion.revision}&attempt=${attempts}`;
        if (video) element.play?.().catch?.(failed);
      };
      const failed = () => {
        if (!current()) return;
        root.classList.remove("dream-motion-ready");
        element.dataset.motionState = "error";
        if (motion.transport === "cdp") { element.__dreamFrameBusy = false; return; }
        if (element.__dreamRetry || attempts >= 5) return;
        element.__dreamRetry = setTimeout(() => {
          element.__dreamRetry = null;
          attempts += 1;
          reload();
        }, 500 * (2 ** attempts));
      };
      element.addEventListener(video ? "canplay" : "load", ready);
      element.addEventListener("error", failed);
      if (video) {
        element.autoplay = true;
        element.muted = true;
        element.loop = true;
        element.playsInline = true;
      }
      document.body.insertBefore(element, document.body.firstChild);
      if (motion.transport !== "cdp") reload();
    }
  };

  const acceptMotionFrame = (base64, revision) => {
    const state = window[STATE_KEY];
    if (state?.installToken !== installToken || window.__CODEX_DREAM_SKIN_DISABLED__ ||
        !config.motion || config.motion.transport !== "cdp" || config.motion.revision !== revision ||
        typeof base64 !== "string" || base64.length > 2800000 || !/^[A-Za-z0-9+/]+={0,2}$/.test(base64)) return false;
    const element = document.getElementById(MOTION_ID);
    if (!element || element.__dreamDisposed || element.__dreamFrameBusy) return false;
    try {
      const url = createArtUrl("data:image/jpeg;base64," + base64);
      element.__dreamFrameUrl = url;
      element.__dreamFrameBusy = true;
      const decoder = new Image();
      element.__dreamDecoder = decoder;
      const finish = () => {
        URL.revokeObjectURL(url);
        if (element.__dreamFrameUrl === url) element.__dreamFrameUrl = null;
        element.__dreamFrameBusy = false;
      };
      decoder.onload = () => {
        try {
          if (element.__dreamDisposed || document.getElementById(MOTION_ID) !== element) return;
          if (element.width !== decoder.naturalWidth) element.width = decoder.naturalWidth;
          if (element.height !== decoder.naturalHeight) element.height = decoder.naturalHeight;
          const context = element.getContext("2d", { alpha: false, desynchronized: true });
          context.drawImage(decoder, 0, 0, element.width, element.height);
          element.__dreamMarkReady();
        } catch { } finally { finish(); }
      };
      decoder.onerror = finish;
      decoder.src = url;
      return true;
    } catch {
      if (element.__dreamFrameUrl) URL.revokeObjectURL(element.__dreamFrameUrl);
      element.__dreamFrameUrl = null;
      element.__dreamFrameBusy = false;
      return false;
    }
  };

  const applyProfile = (root, fullscreenMode = false) => {
    const useFullscreenArt = fullscreenMode && config.fullscreen.enabled && fullscreenArtUrl;
    const focusX = useFullscreenArt ? (config.fullscreen.focusX ?? profile.focusX) : (config.focusX ?? profile.focusX);
    const focusY = useFullscreenArt ? (config.fullscreen.focusY ?? profile.focusY) : (config.focusY ?? profile.focusY);
    const activeAspect = useFullscreenArt ? (config.fullscreen.aspect ?? profile.aspect) : profile.aspect;
    const appearance = config.appearance === "auto" ? detectShellAppearance() : config.appearance;
    const focus = focusX < .4 ? "left" : focusX > .6 ? "right" : "center";
    const safeArea = config.safeArea === "auto" ? (profile.safeArea ||
      (focus === "left" ? "right" : focus === "right" ? "left" : "center")) : config.safeArea;
    const taskMode = config.taskMode === "auto"
      ? profile.aspect >= 2.25 ? "banner" : "ambient"
      : config.taskMode;
    const accent = config.accent || `rgb(${profile.accent.join(" ")})`;
    const accentInk = luminance(...profile.accent) > .42 ? "rgb(26 24 28)" : "rgb(250 248 251)";
    root.classList.toggle("dream-theme-light", appearance === "light");
    root.classList.toggle("dream-theme-dark", appearance === "dark");
    root.classList.toggle("dream-art-visibility-high", config.artVisibility === "high");
    root.classList.toggle("dream-art-visibility-clear", config.artVisibility === "clear");
    root.classList.toggle("dream-controls-railgun", config.controlProfile === "railgun");
    root.classList.toggle("dream-mode-fullscreen", fullscreenMode);
    root.classList.toggle("dream-art-wide", activeAspect >= 1.75);
    root.classList.toggle("dream-art-standard", activeAspect < 1.75);
    for (const value of ["left", "center", "right"]) {
      root.classList.toggle(`dream-focus-${value}`, focus === value);
    }
    for (const value of ["left", "center", "right", "none"]) {
      root.classList.toggle(`dream-safe-${value}`, safeArea === value);
    }
    for (const value of ["ambient", "banner", "off"]) {
      root.classList.toggle(`dream-task-${value}`, taskMode === value);
    }
    root.style.setProperty("--dream-art", `url("${useFullscreenArt ? fullscreenArtUrl : artUrl}")`);
    root.style.setProperty("--dream-art-position", `${Math.round(focusX * 100)}% ${Math.round(focusY * 100)}%`);
    root.style.setProperty("--dream-focus-x", String(focusX));
    root.style.setProperty("--dream-focus-y", String(focusY));
    root.style.setProperty("--dream-accent", accent);
    root.style.setProperty("--dream-accent-ink", accentInk);
    root.style.setProperty("--dream-image-luma", profile.luma.toFixed(3));
    const paletteProperties = {
      canvas: "--dream-canvas",
      surface: "--dream-surface",
      surfaceRaised: "--dream-surface-raised",
      sidebar: "--dream-sidebar",
      text: "--dream-text",
      textMuted: "--dream-text-muted",
      line: "--dream-line",
    };
    for (const [key, property] of Object.entries(paletteProperties)) {
      if (config.palette[key]) root.style.setProperty(property, config.palette[key]);
      else root.style.removeProperty(property);
    }
  };

  const discoverShell = () => {
    // Codex keeps previous conversations mounted. The first shell may belong
    // to a hidden cached page, so select the surface that is actually visible.
    const shellMain = [...document.querySelectorAll(SHELL_SELECTOR)].find((node) => {
      const rect = node.getBoundingClientRect?.();
      const style = getComputedStyle(node);
      return style.display !== "none" && style.visibility !== "hidden" &&
        (!rect || (rect.width >= 80 && rect.height >= 24));
    }) ?? null;
    const composers = [...document.querySelectorAll(
      ".composer-surface-chrome, [data-codex-composer-root], [data-codex-composer]",
    )];
    const composer = composers
      .filter((node) => {
        const rect = node.getBoundingClientRect?.();
        const style = getComputedStyle(node);
        return style.display !== "none" && style.visibility !== "hidden" &&
          (!rect || (rect.width >= 80 && rect.height >= 24));
      })
      .sort((left, right) => (right.getBoundingClientRect?.().bottom ?? 0) -
        (left.getBoundingClientRect?.().bottom ?? 0))[0] ?? null;
    const sidebarCandidates = [
      document.querySelector("aside.app-shell-left-panel"),
      document.querySelector('aside[data-testid="app-shell-floating-left-panel"]'),
      document.querySelector("aside[data-app-shell-left-panel-appearance]"),
      document.querySelector(".app-shell-left-panel"),
      ...document.querySelectorAll("aside"),
    ].filter((node, index, values) => node && values.indexOf(node) === index &&
      !shellMain?.contains?.(node));
    const shellSidebar = sidebarCandidates
      .filter((node) => {
        const rect = node.getBoundingClientRect?.();
        const style = getComputedStyle(node);
        return style.display !== "none" && style.visibility !== "hidden" &&
          (!rect || (rect.width >= 48 && rect.height >= innerHeight * .4 && rect.left < innerWidth * .45));
      })
      .sort((left, right) => (left.getBoundingClientRect?.().left ?? 0) -
        (right.getBoundingClientRect?.().left ?? 0))[0] ?? null;
    return { shellMain, shellSidebar, composer };
  };

  const markShell = ({ shellMain, shellSidebar, composer }) => {
    const assignments = [
      [SHELL_MAIN_CLASS, shellMain],
      [SHELL_SIDEBAR_CLASS, shellSidebar],
      [COMPOSER_CLASS, composer],
    ];
    for (const [className, activeNode] of assignments) {
      for (const node of document.querySelectorAll(`.${className}`)) {
        if (node !== activeNode) node.classList.remove(className);
      }
      activeNode?.classList?.add(className);
    }
  };

  const decorateControls = (composer) => {
    const existing = document.querySelectorAll(`.${SEND_LIGHTNING_CLASS}`);
    for (const button of existing) button.classList.remove(SEND_LIGHTNING_CLASS);
    if (config.controlProfile !== "railgun") return;
    if (!composer) return;
    const candidates = composer.querySelectorAll('button[class~="bg-token-foreground"], button[type="submit"]');
    for (const button of candidates) {
      const identity = [
        button.getAttribute?.("aria-label"),
        button.getAttribute?.("title"),
        button.getAttribute?.("data-testid"),
      ].filter(Boolean).join(" ").toLowerCase();
      const isStop = /(?:stop|cancel|abort|停止|取消|中止)/i.test(identity);
      const isSend = /(?:send|submit|发送|提交)/i.test(identity);
      if (!isStop && (isSend || button.getAttribute?.("aria-busy") !== "true")) {
        button.classList.add(SEND_LIGHTNING_CLASS);
      }
    }
  };

  const ensure = () => {
    try {
      if (window.__CODEX_DREAM_SKIN_DISABLED__) return;
      const root = document.documentElement;
      if (!root || !document.body) return;
      if (document.querySelector("[data-codex-pet-id]") &&
          !document.querySelector("main.main-surface, [data-app-shell-main-surface]")) {
        clearSkinDom();
        return;
      }

      const shell = discoverShell();
      const { shellMain, shellSidebar, composer } = shell;
      const shellContent = composer || document.querySelector('[role="main"]') ||
        document.querySelector('[data-app-shell-main-content-layout], .messaging-root.messaging-embedded');
      if (!shellMain || !shellContent) {
        // Keep the shared media alive through the brief gap between cached
        // pages being hidden and shown. It is still cleaned up on auxiliary UI.
        if (document.querySelector(SHELL_SELECTOR)) return;
        clearSkinDom();
        return;
      }
    markShell(shell);

    const sidebarStyle = shellSidebar ? getComputedStyle(shellSidebar) : null;
    const sidebarRect = shellSidebar?.getBoundingClientRect?.() ?? null;
    const sidebarVisible = Boolean(shellSidebar) && sidebarStyle?.display !== "none" &&
      sidebarStyle?.visibility !== "hidden" && (!sidebarRect || sidebarRect.width >= 48);
    const mainRect = shellMain.getBoundingClientRect?.();
    const fullscreenMode = !sidebarVisible && (!mainRect || mainRect.left < 48);
    const runtimeState = window[STATE_KEY];
    if (runtimeState) runtimeState.mode = fullscreenMode ? "fullscreen" : "standard";

    root.classList.add("codex-dream-skin");
    applyProfile(root, fullscreenMode);
    ensureMotion(root);

    let style = document.getElementById(STYLE_ID);
    if (!style) {
      style = document.createElement("style");
      style.id = STYLE_ID;
      (document.head || root).appendChild(style);
    }
    if (style.dataset.dreamVersion !== "4") {
      style.textContent = compatibleCssText;
      style.dataset.dreamVersion = "4";
    }

    const homeScope = shellMain.querySelector ? shellMain : document;
    const home = homeScope.querySelector('[role="main"]:has([data-testid="home-icon"])') ||
      homeScope.querySelector('[role="main"]:has([data-home-ambient-suggestions])') ||
      homeScope.querySelector("[data-home-ambient-suggestions]")?.closest?.(
        '[role="main"], [data-app-shell-main-content-layout]',
      ) || null;
    const legacyParent = home?.firstElementChild?.firstElementChild;
    const legacyHero = legacyParent?.firstElementChild;
    const legacyHome = Boolean(legacyHero &&
      getComputedStyle(legacyParent).display !== "contents" &&
      !legacyHero.classList?.contains("group/home-takeover") &&
      legacyHero.querySelector?.('[data-feature="game-source"]'));
    for (const candidate of document.querySelectorAll('[role="main"]')) {
      candidate.classList.toggle("dream-home", candidate === home);
      candidate.classList.toggle("dream-home-legacy", candidate === home && legacyHome);
      candidate.classList.toggle("dream-task", candidate !== home);
    }
    const utilityBars = new Set(home ? home.querySelectorAll('[class*="_homeUtilityBar_"]') : []);
    for (const candidate of document.querySelectorAll(`.${HOME_UTILITY_CLASS}`)) {
      if (!utilityBars.has(candidate)) candidate.classList.remove(HOME_UTILITY_CLASS);
    }
    for (const candidate of utilityBars) candidate.classList.add(HOME_UTILITY_CLASS);
    shellMain.classList.toggle("dream-home-shell", Boolean(home));
    decorateControls(composer);

    let chrome = document.getElementById(CHROME_ID);
    if (!chrome || chrome.parentElement !== document.body) {
      chrome?.remove();
      chrome = document.createElement("div");
      chrome.id = CHROME_ID;
      chrome.setAttribute("aria-hidden", "true");
      document.body.appendChild(chrome);
    }
      chrome.classList.toggle("dream-home-shell", Boolean(home));
    } catch {
      // Renderer exceptions must not escape into Codex.
    }
  };

  const cleanup = () => {
    const state = window[STATE_KEY];
    if (state?.installToken !== installToken) return false;
    window.__CODEX_DREAM_SKIN_DISABLED__ = true;
    clearSkinDom();
    state?.observer?.disconnect();
    if (state?.timer) clearInterval(state.timer);
    if (state?.scheduler?.timeout) clearTimeout(state.scheduler.timeout);
    if (state?.artUrl) URL.revokeObjectURL(state.artUrl);
    if (state?.fullscreenArtUrl) URL.revokeObjectURL(state.fullscreenArtUrl);
    delete window[STATE_KEY];
    return true;
  };

  const scheduler = { timeout: null };
  const scheduleEnsure = () => {
    // A streaming chat must not keep postponing a pending route update.
    if (scheduler.timeout) return;
    scheduler.timeout = setTimeout(() => {
      scheduler.timeout = null;
      ensure();
    }, 180);
  };
  observer = new MutationObserver((records) => {
    try {
      if (samplingNativeShell) return;
      // Ignore our palette style writes and unrelated inline style updates.
      // Cached pages can switch visibility using style/hidden without remounting.
      if (records?.length && !records.some((record) =>
        record.attributeName !== "style" ||
        (record.target !== document.documentElement &&
          (record.target.matches?.(SHELL_SELECTOR) || record.target.querySelector?.(SHELL_SELECTOR))))) return;
      scheduleEnsure();
    } catch {}
  });
  observer.observe(document.documentElement, {
    childList: true,
    subtree: true,
    attributes: true,
    attributeFilter: ["class", "style", "hidden", "inert", "data-app-shell-active-page", "data-theme", "data-appearance", "data-color-mode"],
  });
  const timer = setInterval(ensure, 5000);
  window[STATE_KEY] = {
    ensure, cleanup, observer, timer, scheduler, artUrl, fullscreenArtUrl, profile, config, acceptMotionFrame,
    installToken, mode: "standard", version: "1.5.3",
  };
  ensure();
  analyzeArt().then((result) => {
    const state = window[STATE_KEY];
    if (state?.installToken !== installToken || window.__CODEX_DREAM_SKIN_DISABLED__) return;
    profile = result;
    state.profile = result;
    ensure();
  }).catch(() => {});
  return { installed: true, version: "1.5.3", adaptive: true, compat: "26.903" };
})(__DREAM_CSS_JSON__, __DREAM_ART_JSON__, __DREAM_FULLSCREEN_ART_JSON__, __DREAM_THEME_JSON__)
