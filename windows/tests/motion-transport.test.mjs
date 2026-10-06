import assert from 'node:assert/strict';
import { mkdtemp, mkdir, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { loadPayload } from '../scripts/injector.mjs';

const scratchBase=path.resolve(process.env.CODEX_TEST_SCRATCH_ROOT || tmpdir());
const scratch=await mkdtemp(path.join(scratchBase,'motion-transport-'));
try {
  const themeDir=path.join(scratch,'active-theme');
  await mkdir(themeDir);await mkdir(path.join(scratch,'control'));
  const token='a'.repeat(64);
  await writeFile(path.join(scratch,'control','motion-token.txt'),token);
  const candidate=kind=>({imagePath:path.join(themeDir,'poster.png'),imageBytes:Buffer.from('fixture'),fingerprint:'fixture-'+kind,
    theme:{id:'fixture-'+kind,motion:{kind,revision:'b'.repeat(16)}}});
  for(const kind of ['video','scene','web']){
    const loaded=await loadPayload(themeDir,candidate(kind));
    assert.ok(loaded.payload.includes('"transport":"cdp"'),'rendered wallpapers retain their canvas frame transport');
    assert.equal(loaded.motionRelay.token,token);
    assert.equal(loaded.motionRelay.key,'fixture-'+kind);
    assert.ok(!loaded.payload.includes(token),'the token stays in the host relay instead of the renderer');
  }
  await writeFile(path.join(scratch,'control','motion-token.txt'),'invalid');
  await assert.rejects(()=>loadPayload(themeDir,candidate('video')),/motion token is invalid/);
  console.log('PASS: video, scene and web payloads use authenticated bounded frame relays compatible with Owl URL safety.');
} finally {
  if(path.dirname(path.resolve(scratch))!==scratchBase || !path.basename(scratch).startsWith('motion-transport-'))throw new Error('Unsafe test cleanup target');
  await rm(scratch,{recursive:true,force:true});
}
