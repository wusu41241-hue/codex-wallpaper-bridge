import assert from 'node:assert/strict';
import { MotionRelay } from '../scripts/motion-relay.mjs';

const source = { key: 'theme-a', revision: 'a'.repeat(16), token: 'b'.repeat(64) };
const jpeg = new Uint8Array([255,216,1,2,255,217]);
let resolveFrame;
const delivered = new Promise(resolve => { resolveFrame = resolve; });
let requests = 0;
const relay = new MotionRelay(async (frame, revision) => {
  assert.deepEqual(Buffer.from(frame, 'base64'), Buffer.from(jpeg));
  assert.equal(revision, source.revision);
  relay.stop();
  resolveFrame();
}, async (url) => {
  requests++;
  assert.equal(new URL(url).hostname, '127.0.0.1');
  assert.equal(new URL(url).pathname, '/frame');
  return new Response(jpeg);
});
relay.setSource({...source,token:'invalid'});
assert.equal(requests,0);
relay.setSource(source);
await delivered;
assert.equal(requests,1);

let release;
let forwarded = 0;
const pending = new MotionRelay(async()=>{forwarded++;}, async()=>new Promise(resolve=>{release=()=>resolve(new Response(jpeg));}));
pending.setSource(source);
pending.stop();
release();
await new Promise(resolve=>setTimeout(resolve,30));
assert.equal(forwarded,0,'a retired theme must not receive a late frame');
console.log('PASS: local wallpaper frame relay validates input and stops delivery when retired.');
