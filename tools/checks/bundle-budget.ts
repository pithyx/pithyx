// Bundle budgets, gzip compressed, run after `pnpm build`:
// - sign-in: everything index.html loads before the first render. At most 200 KB (desktop/CLAUDE.md).
// - desktop: what signing in adds on top, the desktop chunk with its imports and CSS. The sign-in screen prefetches it.
// Each fails more than 5 percent above its recorded baseline. Baselines only go up with the owner's consent.
//
//   node tools/checks/bundle-budget.ts            check
//   node tools/checks/bundle-budget.ts --update   record the current sizes as the new baselines
import { readFileSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { gzipSync } from 'node:zlib';

const repoRoot = resolve(import.meta.dirname, '..', '..');
const dist = join(repoRoot, 'desktop', 'dist');
// Written by the build (desktop/vite.config.ts, bundleGraph).
const graphFile = join(repoRoot, 'desktop', 'node_modules', '.cache', 'bundle-graph.json');
const baselineFile = join(repoRoot, 'tools', 'checks', 'bundle-budget.json');
const SIGN_IN_LIMIT = 200 * 1024;
const TOLERANCE = 0.05;
const DESKTOP_MODULE = 'src/desktop/Desktop.tsx';

interface Chunk {
  file: string;
  module: string | null;
  imports: string[];
  css: string[];
}

interface Baseline {
  signInGzipBytes: number;
  desktopGzipBytes: number;
}

function gzipBytes(path: string): number {
  return gzipSync(readFileSync(join(dist, path))).length;
}

function kb(bytes: number): string {
  return `${(bytes / 1024).toFixed(1)} KB`;
}

const html = readFileSync(join(dist, 'index.html'), 'utf8');
const signIn = new Set(
  [
    ...html.matchAll(/<script type="module"[^>]*\ssrc="\/([^"]+)"/g),
    ...html.matchAll(/<link rel="(?:modulepreload|stylesheet)"[^>]*\shref="\/([^"]+)"/g),
  ].map((match) => match[1] ?? ''),
);

const graph = JSON.parse(readFileSync(graphFile, 'utf8')) as Chunk[];
const chunks = new Map(graph.map((chunk) => [chunk.file, chunk]));
const desktop = new Set<string>();
function collect(file: string) {
  const chunk = chunks.get(file);
  if (chunk === undefined || desktop.has(file)) {
    return;
  }
  desktop.add(file);
  chunk.css.forEach((css) => desktop.add(css));
  chunk.imports.forEach(collect);
}
const desktopChunk = graph.find((chunk) => chunk.module === DESKTOP_MODULE);
if (desktopChunk === undefined) {
  throw new Error(`No chunk for ${DESKTOP_MODULE}; run pnpm build first`);
}
collect(desktopChunk.file);
signIn.forEach((file) => desktop.delete(file));

function total(label: string, files: Set<string>): number {
  let sum = 0;
  for (const file of files) {
    const size = gzipBytes(file);
    sum += size;
    console.log(`${kb(size).padStart(10)}  ${file}`);
  }
  console.log(`${kb(sum).padStart(10)}  ${label}\n`);
  return sum;
}
const measured: Baseline = {
  signInGzipBytes: total('sign-in total', signIn),
  desktopGzipBytes: total('desktop total, loaded after sign-in', desktop),
};

if (process.argv.includes('--update')) {
  writeFileSync(baselineFile, `${JSON.stringify(measured, null, 2)}\n`);
  console.log(
    `baselines recorded: sign-in ${kb(measured.signInGzipBytes)}, desktop ${kb(measured.desktopGzipBytes)}`,
  );
} else {
  const baseline = JSON.parse(readFileSync(baselineFile, 'utf8')) as Baseline;
  const budgets = [
    ['sign-in', measured.signInGzipBytes, baseline.signInGzipBytes, SIGN_IN_LIMIT],
    ['desktop', measured.desktopGzipBytes, baseline.desktopGzipBytes, Infinity],
  ] as const;
  for (const [name, size, recorded, limit] of budgets) {
    const allowed = Math.min(limit, Math.round(recorded * (1 + TOLERANCE)));
    if (size > allowed) {
      console.error(
        `${name} bundle ${kb(size)} exceeds ${kb(allowed)} (baseline ${kb(recorded)} + 5 percent${limit === Infinity ? '' : `, at most ${kb(limit)}`})`,
      );
      process.exitCode = 1;
    }
  }
}
