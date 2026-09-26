// Writes a declaration with the exact class names of every CSS Module under src/ into .css-types/ (gitignored).
// tsconfig maps .css-types/src onto src through rootDirs, so `styles.name` is a plain string and a typo is a type
// error. Runs before typecheck and lint, and inside the Vite dev server on every change.
import { mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join, relative } from 'node:path';

const desktopRoot = join(import.meta.dirname, '..');
const sourceRoot = join(desktopRoot, 'src');
export const typesRoot = join(desktopRoot, '.css-types');

export function classNames(css: string): string[] {
  const withoutComments = css.replace(/\/\*[\s\S]*?\*\//g, '');
  const names = new Set<string>();
  for (const [, prelude = ''] of withoutComments.matchAll(/([^{};]+)\{/g)) {
    if (prelude.trim().startsWith('@')) {
      continue;
    }
    for (const [, name = ''] of prelude.matchAll(/\.(-?[_a-zA-Z][\w-]*)/g)) {
      names.add(name);
    }
  }
  return [...names].sort();
}

export function declaration(file: string, css: string): string {
  const lines = classNames(css).map((name) => `  readonly ${JSON.stringify(name)}: string;`);
  return [
    `// Generated from ${relative(desktopRoot, file)} by scripts/css-module-types.ts. Do not edit.`,
    'declare const styles: {',
    ...lines,
    '};',
    'export default styles;',
    '',
  ].join('\n');
}

function modules(directory: string): string[] {
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
    const path = join(directory, entry.name);
    if (entry.isDirectory()) {
      return modules(path);
    }
    return entry.name.endsWith('.module.css') ? [path] : [];
  });
}

export function writeDeclaration(file: string) {
  const target = join(typesRoot, `${relative(desktopRoot, file)}.d.ts`);
  mkdirSync(dirname(target), { recursive: true });
  writeFileSync(target, declaration(file, readFileSync(file, 'utf8')));
}

export function generateAll() {
  rmSync(typesRoot, { recursive: true, force: true });
  for (const file of modules(sourceRoot)) {
    writeDeclaration(file);
  }
}

if (process.argv[1] === import.meta.filename) {
  generateAll();
}
