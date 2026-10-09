import { createRequire } from 'node:module';
import { readFile, mkdir, writeFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const require = createRequire(import.meta.url);
// The optional path is useful when regenerating with a preinstalled toolchain.
const sharp = process.env.NYKUR_SHARP_MODULE
  ? require(process.env.NYKUR_SHARP_MODULE)
  : require('sharp');
const root = resolve(here, '../..');
const assets = resolve(root, 'src/NyKurEdge.App/Assets');
const preview = resolve(root, 'artifacts/branding');
const source = await readFile(resolve(here, 'app-icon.svg'));
await mkdir(assets, { recursive: true });
await mkdir(preview, { recursive: true });

async function icon(size) {
  return sharp(source, { density: 384 })
    .resize(size, size, { kernel: 'lanczos3' })
    .png({ compressionLevel: 9 })
    .toBuffer();
}

const squares = [
  ['Square44x44Logo.scale-200.png', 88],
  ['Square44x44Logo.targetsize-24_altform-unplated.png', 24],
  ['Square44x44Logo.targetsize-48_altform-lightunplated.png', 48],
  ['Square150x150Logo.scale-200.png', 300],
  ['StoreLogo.png', 50],
  ['LockScreenLogo.scale-200.png', 48],
];
for (const [filename, size] of squares) {
  await writeFile(resolve(assets, filename), await icon(size));
}

for (const [filename, width, height, size] of [
  ['Wide310x150Logo.scale-200.png', 620, 300, 244],
  ['SplashScreen.scale-200.png', 1240, 600, 320],
]) {
  await sharp({ create: { width, height, channels: 4, background: '#00000000' } })
    .composite([{ input: await icon(size), left: (width - size) / 2, top: (height - size) / 2 }])
    .png({ compressionLevel: 9 })
    .toFile(resolve(assets, filename));
}

// Windows Vista and newer support PNG-compressed ICO images at every size.
// Encoding them directly avoids a second image-processing dependency.
const sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
const images = await Promise.all(sizes.map(icon));
const header = Buffer.alloc(6);
header.writeUInt16LE(1, 2);
header.writeUInt16LE(sizes.length, 4);
const entries = [];
let offset = 6 + sizes.length * 16;
for (let i = 0; i < sizes.length; i += 1) {
  const entry = Buffer.alloc(16);
  entry[0] = sizes[i] === 256 ? 0 : sizes[i];
  entry[1] = entry[0];
  entry.writeUInt16LE(1, 4);
  entry.writeUInt16LE(32, 6);
  entry.writeUInt32LE(images[i].length, 8);
  entry.writeUInt32LE(offset, 12);
  entries.push(entry);
  offset += images[i].length;
}
await writeFile(resolve(assets, 'AppIcon.ico'), Buffer.concat([header, ...entries, ...images]));

await writeFile(resolve(preview, 'app-icon-1024.png'), await icon(1024));
const displaySizes = [16, 24, 32, 48, 64, 128, 256];
const composites = [];
let left = 28;
for (const size of displaySizes) {
  composites.push({ input: await icon(size), left, top: 40 + Math.floor((256 - size) / 2) });
  left += Math.max(size + 28, 60);
}
await sharp({ create: { width: left, height: 336, channels: 4, background: '#e9eef1' } })
  .composite(composites)
  .png({ compressionLevel: 9 })
  .toFile(resolve(preview, 'app-icon-sizes.png'));
console.log(`Generated ${squares.length + 3} native assets from app-icon.svg.`);
console.log(`ICO sizes: ${sizes.join(', ')} px.`);
console.log(`Preview: ${resolve(preview, 'app-icon-sizes.png')}`);
