import assert from 'node:assert/strict';
import { MotionRelay } from '../scripts/motion-relay.mjs';

const source = { key: 'theme-a', revision: 'a'.repeat(16), token: 'b'.repeat(64) };
const jpeg = new Uint8Array([255, 216, 1, 2, 255, 217]);
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
async function eventually(condition, message) {
  const deadline = performance.now() + 2000;
  while (!condition()) {
    if (performance.now() > deadline) assert.fail(message);
    await sleep(5);
  }
}

{
  const requests = [];
  const frames = [];
  const relay = new MotionRelay(async (frame, revision) => {
    frames.push({ frame, revision });
  }, async url => {
    requests.push(new URL(url));
    return new Response(jpeg);
  });
  try {
    relay.setSource({ ...source, token: 'invalid' });
    assert.equal(requests.length, 0);
    relay.setSource(source);
    await eventually(() => frames.length === 1, 'a valid frame was not delivered');
    assert.deepEqual(Buffer.from(frames[0].frame, 'base64'), Buffer.from(jpeg));
    assert.equal(frames[0].revision, source.revision);
    assert.equal(requests[0].hostname, '127.0.0.1');
    assert.equal(requests[0].pathname, '/frame');
    assert.equal(requests[0].searchParams.get('t'), source.token);
  } finally { relay.stop(); }
}

{
  const pending = [];
  const frames = [];
  const relay = new MotionRelay(async (_, revision) => {
    frames.push(revision);
  }, async (_, { signal }) => new Promise(resolve => { pending.push({ resolve, signal }); }));
  try {
    relay.setSource(source);
    relay.setSource({ ...source });
    assert.equal(pending.length, 1, 'an unchanged source must retain its request');
    relay.setSource({ ...source, token: 'c'.repeat(64) });
    assert.equal(pending.length, 2, 'credential rotation must replace a source with the same key');
    assert.equal(pending[0].signal.aborted, true);
    const replacement = { ...source, token: 'c'.repeat(64), revision: 'd'.repeat(16) };
    relay.setSource(replacement);
    assert.equal(pending.length, 3, 'revision changes must replace a source with the same key');
    for (const request of pending) request.resolve(new Response(jpeg));
    await eventually(() => frames.length > 0, 'the current source did not receive its frame');
    assert.deepEqual(frames, [replacement.revision], 'late retired requests must never deliver to the new source');
  } finally { relay.stop(); }
  assert.equal(pending[2].signal.aborted, true);
}

{
  let requests = 0;
  let fetching = 0;
  let delivering = 0;
  let maxFetching = 0;
  let maxDelivering = 0;
  const starts = [];
  const frames = [];
  const relay = new MotionRelay(async () => {
    delivering++;
    maxDelivering = Math.max(maxDelivering, delivering);
    await sleep(18);
    frames.push(performance.now());
    delivering--;
  }, async () => {
    fetching++;
    maxFetching = Math.max(maxFetching, fetching);
    starts.push(performance.now());
    const id = ++requests;
    await sleep(8);
    fetching--;
    return new Response(jpeg, { headers: { 'X-Dream-Frame-Id': `host:${id}` } });
  });
  try {
    relay.setSource(source);
    await eventually(() => frames.length >= 6, 'the relay did not sustain paced deliveries');
    assert.equal(maxFetching, 1, 'frame requests must not overlap');
    assert.equal(maxDelivering, 1, 'renderer deliveries must not overlap');
    const averageInterval = (starts[5] - starts[0]) / 5;
    assert.ok(averageInterval >= 25, `requests exceeded their 30 fps budget (${averageInterval} ms)`);
    assert.ok(averageInterval < 80, `fetch/CDP duration was added to a fixed post-frame delay (${averageInterval} ms)`);
    console.log(`PASS: motion relay pacing ${(1000 / averageInterval).toFixed(1)} fps with single-request and single-delivery backpressure.`);
  } finally { relay.stop(); }
}

{
  const requests = [];
  const frames = [];
  const responses = [
    () => new Response(jpeg, { headers: { 'X-Dream-Frame-Id': 'epoch:1' } }),
    () => new Response(null, { status: 204 }),
    () => new Response(jpeg, { headers: { 'X-Dream-Frame-Id': 'epoch:1' } }),
    () => new Response(jpeg, { headers: { 'X-Dream-Frame-Id': 'epoch:2' } }),
  ];
  const relay = new MotionRelay(async frame => { frames.push(frame); }, async url => {
    requests.push(new URL(url));
    return responses.shift()();
  });
  try {
    relay.setSource(source);
    await eventually(() => frames.length === 2, 'the host cursor did not advance to the next frame');
    assert.equal(requests.length, 4);
    assert.equal(requests[0].searchParams.has('after'), false);
    assert.ok(requests.slice(1).every(url => url.searchParams.get('after') === 'epoch:1'));
    assert.equal(relay.getMetrics().skipped, 2, '204 and the same frame cursor must skip decoding/delivery');
  } finally { relay.stop(); }
}

{
  let requests = 0;
  const frames = [];
  const relay = new MotionRelay(async frame => { frames.push(frame); }, async () => {
    requests++;
    return new Response(requests < 3 ? jpeg : new Uint8Array([255, 216, 3, 4, 255, 217]));
  });
  try {
    relay.setSource(source);
    await eventually(() => frames.length === 2, 'the legacy host did not deliver its changed JPEG');
    assert.equal(requests, 3);
    assert.equal(relay.getMetrics().skipped, 1, 'a legacy host must not re-encode/redraw its cached identical JPEG');
  } finally { relay.stop(); }
}

{
  let receivers = false;
  let requests = 0;
  let frames = 0;
  const relay = new MotionRelay(async () => { frames++; }, async () => {
    requests++;
    return new Response(jpeg);
  }, { hasReceivers: () => receivers, idlePollMs: 20 });
  try {
    relay.setSource(source);
    await sleep(70);
    assert.equal(requests, 0, 'no receiver means no HTTP request or frame encoding');
    receivers = true;
    await eventually(() => frames > 0, 'the relay failed to wake for a receiver');
    receivers = false;
    const previousRequests = requests;
    await sleep(70);
    assert.equal(requests, previousRequests, 'the relay failed to idle after its last receiver left');
  } finally { relay.stop(); }
}

{
  let accepted = false;
  let requests = 0;
  let attempts = 0;
  const attemptTimes = [];
  const relay = new MotionRelay(async () => {
    attempts++;
    attemptTimes.push(performance.now());
    if (attempts === 2) accepted = true;
    return accepted;
  }, async () => {
    requests++;
    return new Response(jpeg, { headers: { 'X-Dream-Frame-Id': 'epoch:1' } });
  }, { idlePollMs: 50 });
  try {
    relay.setSource(source);
    await eventually(() => attempts === 1, 'the renderer did not receive its initial frame');
    await eventually(() => relay.getMetrics().delivered === 1, 'a rejected frame cursor must be retried after the renderer becomes ready');
    assert.equal(attempts, 2);
    assert.equal(requests, 2);
    assert.ok(attemptTimes[1] - attemptTimes[0] >= 25, 'a frame rejected by all renderers must back off for a frame budget');
  } finally { relay.stop(); }
}

{
  const capturedLogs = [];
  const originalError = console.error;
  const requestTimes = [];
  let delivered = 0;
  console.error = (...args) => capturedLogs.push(args.join(' '));
  const relay = new MotionRelay(async () => { delivered++; }, async () => {
    requestTimes.push(performance.now());
    return requestTimes.length < 3 ? new Response('not-ready', { status: 404 }) : new Response(jpeg);
  });
  try {
    relay.setSource(source);
    await eventually(() => delivered > 0, 'the relay failed to recover from a warming host');
    assert.ok(requestTimes[1] - requestTimes[0] >= 85, 'a failing host must not be polled in a busy loop');
    assert.ok(requestTimes[2] - requestTimes[1] >= 180, 'repeated failure must increase the recovery delay');
    assert.equal(relay.getMetrics().errors, 2);
    assert.equal(capturedLogs.length, 1, 'recovery logs must be throttled');
  } finally {
    relay.stop();
    console.error = originalError;
  }
}

console.log('PASS: motion relay validates frames, retires old generations, skips duplicate cursors, idles without receivers, and backs off on errors.');
