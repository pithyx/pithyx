// Initial desktop bundle budget (desktop/CLAUDE.md): everything index.html loads before the first render, gzip
// compressed. Fails above 200 KB or more than 5 percent above the recorded baseline. Run after `pnpm build`.
//
//   node tools/checks/bundle-budget.ts            check
//   node tools/checks/bundle-budget.ts --update   record the current size as the new baseline
import { readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { gzipSync } from 'node:zlib';

const repoRoot = resolve(import.meta.dirname, '..', '..');
const dist = join(repoRoot, 'desktop', 'dist');
const baselineFile = join(repoRoot, 'tools', 'checks', 'bundle-budget.json');
const LIMIT = 200 * 1024;
const TOLERANCE = 0.05;

interface Baseline {
  initialGzipBytes: number;
}

function gzipBytes(path: string): number {
  return gzipSync(readFileSync(join(dist, path))).length;
}

function kb(bytes: number): string {
  return `${(bytes / 1024).toFixed(1)} KB`;
}

const html = readFileSync(join(dist, 'index.html'), 'utf8');
const initial = [
  ...html.matchAll(/<script type="module"[^>]*\ssrc="\/([^"]+)"/g),
  ...html.matchAll(/<link rel="(?:modulepreload|stylesheet)"[^>]*\shref="\/([^"]+)"/g),
].map((match) => match[1] ?? '');

let total = 0;
for (const file of initial) {
  const size = gzipBytes(file);
  total += size;
  console.log(`${kb(size).padStart(10)}  ${file}`);
}
console.log(`${kb(total).padStart(10)}  initial total (limit ${kb(LIMIT)})`);

const lazy = readdirSync(join(dist, 'assets')).filter(
  (file) => /^Desktop-.*\.(js|css)$/.test(file) && !initial.includes(`assets/${file}`),
);
for (const file of lazy) {
  console.log(
    `${kb(gzipBytes(join('assets', file))).padStart(10)}  assets/${file} (after sign-in)`,
  );
}

if (process.argv.includes('--update')) {
  writeFileSync(baselineFile, `${JSON.stringify({ initialGzipBytes: total }, null, 2)}\n`);
  console.log(`baseline recorded: ${kb(total)}`);
} else {
  const baseline = JSON.parse(readFileSync(baselineFile, 'utf8')) as Baseline;
  const allowed = Math.min(LIMIT, Math.round(baseline.initialGzipBytes * (1 + TOLERANCE)));
  if (total > allowed) {
    console.error(
      `initial bundle ${kb(total)} exceeds ${kb(allowed)} (baseline ${kb(baseline.initialGzipBytes)} + 5 percent, at most ${kb(LIMIT)})`,
    );
    process.exitCode = 1;
  }
}
