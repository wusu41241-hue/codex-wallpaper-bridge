// Transfer only the user's selected local wallpaper frames over the already
// verified skin connection. The Codex renderer never opens a local HTTP URL.
const MAX_FRAME_BYTES = 2 * 1024 * 1024;

function wait(ms, signal) {
  if (signal.aborted || ms <= 0) return Promise.resolve();
  return new Promise(resolve => {
    const finish = () => {
      clearTimeout(timer);
      signal.removeEventListener('abort', finish);
      resolve();
    };
    const timer = setTimeout(finish, Math.ceil(ms));
    signal.addEventListener('abort', finish, { once: true });
  });
}

export class MotionRelay {
  constructor(deliver, fetchFrame = fetch, options = {}) {
    this.deliver = deliver;
    this.fetchFrame = fetchFrame;
    this.hasReceivers = options.hasReceivers ?? (() => true);
    this.frameIntervalMs = 1000 / Math.max(1, Math.min(60, options.fps ?? 30));
    this.idlePollMs = options.idlePollMs ?? 200;
    this.key = null;
    this.sourceIdentity = null;
    this.controller = null;
    this.generation = 0;
    this.metrics = {
      requests: 0, frames: 0, delivered: 0, skipped: 0, errors: 0,
      bytes: 0, fetchMs: 0, deliveryMs: 0,
    };
  }

  getMetrics() { return { ...this.metrics }; }

  setSource(source) {
    const valid = source && /^[a-f0-9]{64}$/.test(source.token) && /^[a-f0-9]{16}$/.test(source.revision);
    const identity = valid ? `${source.key}\n${source.revision}\n${source.token}` : null;
    if (this.sourceIdentity === identity) return;
    this.stop();
    if (!valid) return;
    this.key = source.key;
    this.sourceIdentity = identity;
    this.controller = new AbortController();
    const generation = this.generation;
    this.run({ ...source }, this.controller.signal, generation).catch(() => {});
  }

  stop() {
    this.generation++;
    this.controller?.abort();
    this.controller = null;
    this.key = null;
    this.sourceIdentity = null;
  }

  async run(source, signal, generation) {
    const url = `http://127.0.0.1:47866/frame?t=${source.token}`;
    const active = () => !signal.aborted && generation === this.generation;
    let frameId = null;
    let lastFrame = null;
    let errorCount = 0;
    let lastErrorLog = -Infinity;
    while (active()) {
      if (!this.hasReceivers()) {
        await wait(this.idlePollMs, signal);
        continue;
      }
      const startedAt = performance.now();
      let delay = 0;
      try {
        // One fetch and one delivery may be in flight. A slow renderer applies
        // backpressure; the next request takes the latest frame, never a queue.
        this.metrics.requests++;
        const response = await this.fetchFrame(frameId ? `${url}&after=${encodeURIComponent(frameId)}` : url, {
          signal: AbortSignal.any([signal, AbortSignal.timeout(8000)]), cache: 'no-store',
        });
        if (!active()) {
          await response.body?.cancel().catch(() => {});
          break;
        }
        if (response.status === 204 || response.status === 304) {
          this.metrics.skipped++;
          errorCount = 0;
        } else {
          if (!response.ok) {
            await response.body?.cancel().catch(() => {});
            throw new Error('frame-not-ready');
          }
          const candidateId = response.headers?.get('X-Dream-Frame-Id');
          const nextFrameId = candidateId && /^[a-zA-Z0-9._:-]{1,128}$/.test(candidateId) ? candidateId : null;
          if (nextFrameId && nextFrameId === frameId) {
            await response.body?.cancel().catch(() => {});
            this.metrics.skipped++;
            errorCount = 0;
          } else {
            const declaredSize = Number(response.headers?.get('Content-Length'));
            if (declaredSize > MAX_FRAME_BYTES) {
              await response.body?.cancel().catch(() => {});
              throw new Error('frame-too-large');
            }
            const reader = response.body.getReader();
            let size = 0;
            const chunks = [];
            try {
              while (active()) {
                const { value, done } = await reader.read();
                if (done) break;
                size += value.length;
                if (size > MAX_FRAME_BYTES) throw new Error('frame-too-large');
                chunks.push(Buffer.from(value.buffer, value.byteOffset, value.byteLength));
              }
            } finally { await reader.cancel().catch(() => {}); }
            if (!active()) break;
            const frame = chunks.length === 1 ? chunks[0] : Buffer.concat(chunks, size);
            if (frame.length < 4 || frame[0] !== 0xff || frame[1] !== 0xd8 || frame.at(-2) !== 0xff || frame.at(-1) !== 0xd9) throw new Error('invalid-jpeg');
            this.metrics.fetchMs += performance.now() - startedAt;
            this.metrics.bytes += frame.length;
            this.metrics.frames++;
            // Older hosts have no cursor. Avoid encoding/drawing their same
            // cached JPEG repeatedly while retaining compatibility with them.
            if (!nextFrameId && lastFrame?.equals(frame)) {
              this.metrics.skipped++;
              errorCount = 0;
            } else if (this.hasReceivers()) {
              const deliveredAt = performance.now();
              const accepted = await this.deliver(frame.toString('base64'), source.revision);
              this.metrics.deliveryMs += performance.now() - deliveredAt;
              if (!active()) break;
              errorCount = 0;
              if (accepted === false) delay = this.frameIntervalMs;
              else {
                this.metrics.delivered++;
                frameId = nextFrameId;
                lastFrame = frame;
              }
            } else errorCount = 0;
          }
        }
      } catch {
        if (!active()) break;
        this.metrics.errors++;
        errorCount++;
        delay = Math.min(2000, 100 * (2 ** Math.min(errorCount - 1, 5)));
        if (Date.now() - lastErrorLog > 30000) {
          console.error('[dream-skin] waiting for local wallpaper frame');
          lastErrorLog = Date.now();
        }
      }
      // Pace from the start of this request, not after fetch/CDP completion.
      // Slow captures skip the wait rather than accumulating catch-up work.
      if (active()) await wait(Math.max(delay, startedAt + this.frameIntervalMs - performance.now()), signal);
    }
  }
}
