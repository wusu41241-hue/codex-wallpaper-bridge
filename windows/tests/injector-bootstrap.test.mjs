import assert from "node:assert/strict";
import fs from "node:fs/promises";
import path from "node:path";
import vm from "node:vm";
import { fileURLToPath } from "node:url";
import { earlyPayloadFor, CODEX_SELECTORS } from "../scripts/injector.mjs";

const here = path.dirname(fileURLToPath(import.meta.url));
const injectorPath = path.resolve(here, "../scripts/injector.mjs");
const source = await fs.readFile(injectorPath, "utf8");

function createFixture() {
  const observers = [];
  const timers = new Map();
  let nextTimer = 1;
  const markers = { shell: false, sidebar: false, content: false };
  const context = {
    window: { installs: [] },
    document: {
      documentElement: {},
      body: {},
      querySelector(selector) {
        if (selector === CODEX_SELECTORS.pet) return null;
        if (selector === CODEX_SELECTORS.shell) return markers.shell ? {} : null;
        if (selector === CODEX_SELECTORS.sidebar) return markers.sidebar ? {} : null;
        if (selector === CODEX_SELECTORS.composer || selector === CODEX_SELECTORS.main) {
          return markers.content ? {} : null;
        }
        return null;
      },
      querySelectorAll() { return []; },
    },
    MutationObserver: class {
      constructor(callback) {
        this.callback = callback;
        this.connected = true;
        observers.push(this);
      }
      observe() {}
      disconnect() { this.connected = false; }
    },
    setTimeout(callback) {
      const id = nextTimer++;
      timers.set(id, callback);
      return id;
    },
    clearTimeout(id) { timers.delete(id); },
    setInterval(callback) {
      const id = nextTimer++;
      timers.set(id, callback);
      return id;
    },
    clearInterval(id) { timers.delete(id); },
  };
  context.window.window = context.window;
  context.window.document = context.document;
  context.window.eval = (expression) => vm.runInNewContext(expression, context);
  return { context, markers, observers };
}

const guarded = createFixture();
vm.runInNewContext(earlyPayloadFor('window.installs.push("guarded")', "guarded"), guarded.context);
assert.deepEqual(guarded.context.window.installs, [], "Auxiliary app targets must remain untouched.");
guarded.markers.shell = true;
guarded.observers[0].callback([]);
assert.deepEqual(guarded.context.window.installs, [], "A main surface without Codex content is not sufficient.");
guarded.markers.content = true;
guarded.observers[0].callback([]);
assert.deepEqual(guarded.context.window.installs, ["guarded"],
  "The guarded payload should install for a complete fullscreen shell without a sidebar.");

const generations = createFixture();
vm.runInNewContext(earlyPayloadFor('window.installs.push("old")', "old"), generations.context);
vm.runInNewContext(earlyPayloadFor('window.installs.push("new")', "new"), generations.context);
generations.markers.shell = true;
generations.markers.content = true;
for (const observer of generations.observers) observer.callback([]);
assert.deepEqual(
  generations.context.window.installs,
  ["new"],
  "A stale early script must yield to the newest watcher generation.",
);
assert.equal(generations.context.window.__CODEX_DREAM_SKIN_EARLY_APPLIED__, "new");

const registrationStart = source.indexOf("earlyScriptId = await registerEarlyPayload");
const evaluateStart = source.indexOf("await session.evaluate(earlyPayloadFor", registrationStart);
const probeStart = source.indexOf("const probe = await waitForCodexProbe", registrationStart);
assert.ok(registrationStart >= 0 && evaluateStart > registrationStart && probeStart > evaluateStart,
  "New targets must register and run the early payload before full shell probing.");
assert.match(source, /if \(earlyInjectionFallback\) attachLoadFallback\(/,
  "Load-event reinjection must be attached only when early injection falls back.");
assert.match(source, /if \(!fallbackTargets\.get\(id\)\) return;/,
  "Fallback listeners must stay inert after a successful early registration.");
assert.match(source, /Page\.removeScriptToEvaluateOnNewDocument/,
  "Watcher shutdown and theme refresh must unregister persistent Page scripts.");
assert.match(source, /home:\s*!result\.homePresent\s*\|\|\s*\(Boolean\(result\.home\)/,
  "Home verification must use the route container instead of a brittle nested hero selector.");
assert.doesNotMatch(source, /!result\.homePresent\s*\|\|\s*\(Boolean\(result\.hero\)/,
  "A Codex home DOM update must not fail startup only because the legacy nested hero is absent.");
assert.match(source, /paletteMatchesExpected/,
  "Live verification must compare the runtime palette with the active theme.");
assert.match(source, /palette:\s*result\.paletteMatchesExpected/,
  "A stale runtime palette must block verification.");
assert.match(source, /composer:\s*!result\.composer\s*\|\|\s*result\.composerVisible/,
  "Routes without a composer must not be treated as failed skin injection.");
assert.doesNotMatch(source, /composer:\s*result\.composerVisible/,
  "Composer visibility must only be required when a composer exists on the current route.");
assert.match(source, /main:\s*result\.mainPresent/,
  "Live verification must still require the Codex main content surface.");
assert.match(source, /data-app-shell-main-surface/,
  "Injector discovery must accept Codex 26.903 surfaces that are not a main tag.");
assert.match(source, /data-codex-composer/,
  "Injector discovery must accept the 26.903 composer marker.");
assert.match(source, /data-codex-pet-id/,
  "Injector discovery must skip Codex pet windows.");
assert.match(source, /Refusing CDP method that can crash Codex/,
  "The injector must refuse Debugger/Input/Emulation commands that can crash Codex.");
assert.doesNotMatch(source, /Debugger\.enable|Input\.dispatch|Emulation\./,
  "The injector must not send crash-prone CDP domains.");

console.log("PASS: Windows early injection is shell-guarded, generation-safe, ordered before probing, and fallback-scoped.");
