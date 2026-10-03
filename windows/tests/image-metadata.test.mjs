import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import fs from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import {
  MAX_IMAGE_DIMENSION,
  MAX_IMAGE_PIXELS,
  classifyImageDimensions,
  readImageMetadata,
} from "../scripts/image-metadata.mjs";

const here = path.dirname(fileURLToPath(import.meta.url));
const windowsRoot = path.resolve(here, "..");
const featuredPath = path.join(windowsRoot, "assets", "default-background.png");
const featured = await fs.readFile(featuredPath);
const helper = path.join(windowsRoot, "scripts", "image-metadata.mjs");

assert.deepEqual(readImageMetadata(featured, ".png"), {
  width: 1600,
  height: 900,
  ratio: 1600 / 900,
  wide: true,
  aspect: "wide",
  taskMode: "ambient",
});

const cli = spawnSync(process.execPath, [helper, "--check", featuredPath], {
  encoding: "utf8",
});
assert.equal(cli.status, 0);
assert.deepEqual(JSON.parse(cli.stdout), readImageMetadata(featured, ".png"));

// Minimal SOI/SOF header: exercise JPEG metadata without distributing a photo.
const jpegHeader = Buffer.from([0xff, 0xd8, 0xff, 0xc0, 0x00, 0x0b, 0x08,
  0x05, 0xa0, 0x0a, 0x00, 0x01, 0x01, 0x11, 0x00, 0xff, 0xd9]);
assert.deepEqual(readImageMetadata(jpegHeader, ".jpg"), classifyImageDimensions({ width: 2560, height: 1440 }));

assert.deepEqual(classifyImageDimensions({ width: 800, height: 1200 }), {
  width: 800,
  height: 1200,
  ratio: 800 / 1200,
  wide: false,
  aspect: "portrait",
  taskMode: "ambient",
});
assert.equal(MAX_IMAGE_DIMENSION, 16384);
assert.equal(MAX_IMAGE_PIXELS, 50_000_000);
assert.equal(classifyImageDimensions({ width: 10000, height: 6000 }), null);
assert.equal(classifyImageDimensions({ width: 20000, height: 1 }), null);
assert.equal(classifyImageDimensions({ width: 2560.5, height: 1440 }), null);

const oversizedPngHeader = Buffer.alloc(24);
Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]).copy(oversizedPngHeader);
oversizedPngHeader.writeUInt32BE(13, 8);
oversizedPngHeader.write("IHDR", 12, "ascii");
oversizedPngHeader.writeUInt32BE(10000, 16);
oversizedPngHeader.writeUInt32BE(6000, 20);
assert.equal(readImageMetadata(oversizedPngHeader, ".png"), null);

const malformedJpeg = Buffer.from(jpegHeader);
malformedJpeg[0] = 0;
assert.equal(readImageMetadata(malformedJpeg, ".jpg"), null);

console.log("PASS: Windows injector reads strict image dimensions before building the payload.");
