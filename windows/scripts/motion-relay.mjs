// Transfer only the user's selected local wallpaper frames over the already
// verified skin connection. The Codex renderer never opens a local HTTP URL.
export class MotionRelay {
  constructor(deliver, fetchFrame = fetch) {
    this.deliver = deliver;
    this.fetchFrame = fetchFrame;
    this.key = null;
    this.controller = null;
  }

  setSource(source) {
    const key = source ? source.key : null;
    if (this.key === key) return;
    this.stop();
    if (!source) return;
    if (!/^[a-f0-9]{64}$/.test(source.token) || !/^[a-f0-9]{16}$/.test(source.revision)) return;
    this.key = key;
    this.controller = new AbortController();
    this.run(source, this.controller.signal).catch(() => {});
  }

  stop() {
    this.controller?.abort();
    this.controller = null;
    this.key = null;
  }

  async run(source, signal) {
    const url = `http://127.0.0.1:47866/frame?t=${source.token}`;
    let lastErrorLog = 0;
    while (!signal.aborted) {
      let delay = 120;
      try {
        const response = await this.fetchFrame(url, { signal: AbortSignal.any([signal, AbortSignal.timeout(8000)]), cache: 'no-store' });
        if (!response.ok) throw new Error('frame-not-ready');
        const reader = response.body.getReader();
        let size = 0;
        const chunks = [];
        try {
          while (true) {
            const { value, done } = await reader.read();
            if (done) break;
            size += value.length;
            if (size > 2 * 1024 * 1024) throw new Error('frame-too-large');
            chunks.push(Buffer.from(value));
          }
        } finally { await reader.cancel().catch(() => {}); }
        const frame = Buffer.concat(chunks);
        if (frame.length < 4 || frame[0] !== 0xff || frame[1] !== 0xd8 || frame.at(-2) !== 0xff || frame.at(-1) !== 0xd9) throw new Error('invalid-jpeg');
        if (!signal.aborted) await this.deliver(frame.toString('base64'), source.revision);
      } catch {
        if (signal.aborted) break;
        delay = 1000;
        if (Date.now() - lastErrorLog > 30000) {
          console.error('[dream-skin] waiting for local wallpaper frame');
          lastErrorLog = Date.now();
        }
      }
      if (!signal.aborted) await new Promise(resolve => setTimeout(resolve, delay));
    }
  }
}
