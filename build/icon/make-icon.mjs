// Renders src/SiftDB.Host/Assets/SiftDB.svg into SiftDB.ico (PNG-compressed entries, the standard Windows sizes).
// Run after changing the SVG:  cd build/icon && npm install && node make-icon.mjs
import sharp from 'sharp';
import { readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const assets = fileURLToPath(new URL('../../src/SiftDB.Host/Assets/', import.meta.url));
const svg = readFileSync(assets + 'SiftDB.svg');
const sizes = [16, 20, 24, 32, 40, 48, 64, 256];
const pngs = await Promise.all(sizes.map((s) => sharp(svg, { density: 384 }).resize(s, s).png().toBuffer()));

const header = Buffer.alloc(6 + 16 * sizes.length);
header.writeUInt16LE(0, 0);            // reserved
header.writeUInt16LE(1, 2);            // 1 = icon
header.writeUInt16LE(sizes.length, 4);
let offset = header.length;
sizes.forEach((s, i) => {
  const e = 6 + 16 * i;
  header.writeUInt8(s === 256 ? 0 : s, e);      // width (0 means 256)
  header.writeUInt8(s === 256 ? 0 : s, e + 1);  // height
  header.writeUInt16LE(1, e + 4);               // colour planes
  header.writeUInt16LE(32, e + 6);              // bits per pixel
  header.writeUInt32LE(pngs[i].length, e + 8);
  header.writeUInt32LE(offset, e + 12);
  offset += pngs[i].length;
});
writeFileSync(assets + 'SiftDB.ico', Buffer.concat([header, ...pngs]));
console.log(`SiftDB.ico: ${sizes.join(', ')} px`);
