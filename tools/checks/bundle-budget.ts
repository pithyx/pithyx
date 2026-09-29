// Bundle budgets, gzip compressed, run after `pnpm build`:
// - sign-in: everything index.html loads before the first render. At most 200 KB (desktop/CLAUDE.md), and it fails more
//   than 5 percent above its recorded baseline. The baseline only goes up with the owner's consent.
// - desktop: what signing in adds on top, the desktop chunk with its imports and CSS. The sign-in screen prefetches it.
//   A fixed limit of 150 KB (owner's decision of 2026-09-29): the timing budgets of e2e/tests/budgets.spec.ts decide.
// Both count the texts of the largest language: only the active language's chunks (i18n-<lang> before the first
// render, i18n-<lang>-desktop with the desktop) are loaded.
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
const DESKTOP_LIMIT = 150 * 1024;
const TOLERANCE = 0.05;
const DESKTOP_MODULE = 'src/desktop/Desktop.tsx';

interface Chunk {
  file: string;
  name: string;
  module: string | null;
  imports: string[];
  css: string[];
}

interface Baseline {
  signInGzipBytes: number;
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

/** Adds the largest of the chunks whose name matches, one per language. */
function addLargestTranslation(files: Set<string>, name: RegExp) {
  const candidates = graph.filter((chunk) => name.test(chunk.name));
  if (candidates.length === 0) {
    throw new Error(`No translation chunk matches ${String(name)}`);
  }
  const largest = candidates.reduce((a, b) => (gzipBytes(b.file) > gzipBytes(a.file) ? b : a));
  files.add(largest.file);
}
addLargestTranslation(signIn, /^i18n-[a-z]+$/);
addLargestTranslation(desktop, /^i18n-[a-z]+-desktop$/);

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
const signInBytes = total('sign-in total', signIn);
const desktopBytes = total('desktop total, loaded after sign-in', desktop);

if (process.argv.includes('--update')) {
  writeFileSync(
    baselineFile,
    `${JSON.stringify({ signInGzipBytes: signInBytes } satisfies Baseline, null, 2)}\n`,
  );
  console.log(`sign-in baseline recorded: ${kb(signInBytes)}`);
} else {
  const baseline = JSON.parse(readFileSync(baselineFile, 'utf8')) as Baseline;
  const signInAllowed = Math.min(
    SIGN_IN_LIMIT,
    Math.round(baseline.signInGzipBytes * (1 + TOLERANCE)),
  );
  if (signInBytes > signInAllowed) {
    console.error(
      `sign-in bundle ${kb(signInBytes)} exceeds ${kb(signInAllowed)} (baseline ${kb(baseline.signInGzipBytes)} + 5 percent, at most ${kb(SIGN_IN_LIMIT)})`,
    );
    process.exitCode = 1;
  }
  if (desktopBytes > DESKTOP_LIMIT) {
    console.error(`desktop bundle ${kb(desktopBytes)} exceeds ${kb(DESKTOP_LIMIT)}`);
    process.exitCode = 1;
  }
}
