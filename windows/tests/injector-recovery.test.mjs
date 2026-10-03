import assert from 'node:assert/strict';
import vm from 'node:vm';
import { visibleSkinNode, rendererHealthExpression } from '../scripts/injector.mjs';

const node = (rect, display = 'block') => ({
  getBoundingClientRect: () => rect,
  display,
});
const rect = { left: 300, top: 600, right: 1100, bottom: 760, width: 800, height: 160 };
const oldComposer = node({ left: 0, top: 0, right: 0, bottom: 0, width: 0, height: 0 });
const offscreen = node({ ...rect, left: -1500, right: -700 });
const hidden = node(rect, 'none');
const activeComposer = node(rect);
const doc = {
  defaultView: { innerWidth: 1280, innerHeight: 820, getComputedStyle: e => ({ display: e.display, visibility: 'visible' }) },
  querySelectorAll: () => [oldComposer, offscreen, hidden, activeComposer],
};
assert.equal(visibleSkinNode(doc, 'composer'), activeComposer,
  'hidden cached inputs preceding the active input must not fail startup');
doc.querySelectorAll = () => [oldComposer, hidden];
assert.equal(visibleSkinNode(doc, 'composer'), null,
  'a route with only cached inputs has no active input');

let ensureCalls = 0;
const elements = new Map();
const ctx = { window: {}, document: { getElementById: id => elements.get(id) } };
const expression = rendererHealthExpression('test-version');
assert.equal(vm.runInNewContext(expression, ctx), false, 'a replaced document requires reinjection');
ctx.window.__CODEX_DREAM_SKIN_STATE__ = {
  version: 'test-version', config: { motion: { kind: 'video' } },
  ensure() { ensureCalls++; elements.set('codex-dream-skin-style', {}); elements.set('codex-dream-skin-chrome', {}); },
};
assert.equal(vm.runInNewContext(expression, ctx), false, 'a missing media layer must be recovered');
elements.set('codex-dream-skin-motion', {});
assert.equal(vm.runInNewContext(expression, ctx), true, 'healthy renderers retain their current playback layer');
assert.equal(ensureCalls, 2);
ctx.window.__CODEX_DREAM_SKIN_DISABLED__ = true;
assert.equal(vm.runInNewContext(expression, ctx), false);
assert.equal(ensureCalls, 2, 'disabled renderers must not be enabled by a health read');
console.log('PASS: cached routes cannot fail startup; document and media replacement are detected.');
