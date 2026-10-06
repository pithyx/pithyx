// Packs the built probe into dist/sdk-probe.pithyxapp: a gzip-compressed ustar archive with pithyx.json, icon.svg and
// frontend/, the layout of D301. A stand-in for `pithyx pack` until @pithyx/cli has it; sorted and without times or
// owners, so the same build gives the same file.
import { readdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { join, relative, sep } from 'node:path';
import { gzipSync } from 'node:zlib';

const root = import.meta.dirname;
const dist = join(root, 'dist');

function files(directory: string): string[] {
  return readdirSync(directory)
    .sort()
    .flatMap((name) => {
      const path = join(directory, name);
      return statSync(path).isDirectory() ? files(path) : [path];
    });
}

function header(name: string, size: number): Buffer {
  const block = Buffer.alloc(512);
  const field = (offset: number, length: number, value: string) => {
    block.write(value, offset, length, 'ascii');
  };
  field(0, 100, name);
  field(100, 8, '0000644\0');
  field(108, 8, '0000000\0');
  field(116, 8, '0000000\0');
  field(124, 12, `${size.toString(8).padStart(11, '0')}\0`);
  field(136, 12, '00000000000\0');
  field(148, 8, '        ');
  block[156] = '0'.charCodeAt(0);
  field(257, 8, 'ustar\u000000');
  const sum = block.reduce((total, value) => total + value, 0);
  field(148, 8, `${sum.toString(8).padStart(6, '0')}\0 `);
  return block;
}

const entries: [string, Buffer][] = [
  ['pithyx.json', readFileSync(join(root, 'pithyx.json'))],
  ['icon.svg', readFileSync(join(root, 'icon.svg'))],
  ...files(join(dist, 'frontend')).map((path): [string, Buffer] => [
    relative(dist, path).split(sep).join('/'),
    readFileSync(path),
  ]),
];
const blocks = entries.flatMap(([name, content]) => [
  header(name, content.length),
  content,
  Buffer.alloc((512 - (content.length % 512)) % 512),
]);
writeFileSync(
  join(dist, 'sdk-probe.pithyxapp'),
  gzipSync(Buffer.concat([...blocks, Buffer.alloc(1024)])),
);
