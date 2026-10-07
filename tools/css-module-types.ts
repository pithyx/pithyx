// Writes a declaration with the exact class names of every CSS Module under <package>/src into <package>/.css-types/
// (gitignored). Each tsconfig maps .css-types/src onto src through rootDirs, so `styles.name` is a plain string and a
// typo is a type error. Runs before typecheck and lint, and inside the desktop's Vite dev server on every change.
//
//   node tools/css-module-types.ts [<package> ...]   every package below when none is given
import {
  existsSync,
  mkdirSync,
  readFileSync,
  readdirSync,
  renameSync,
  rmSync,
  writeFileSync,
} from 'node:fs';
import { dirname, join, relative, sep } from 'node:path';

const repoRoot = join(import.meta.dirname, '..');

// The desktop compiles @pithyx/ui from source, so its type check needs the package's declarations as well.
export const cssModulePackages = ['desktop', 'packages/ui'];

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
    `// Generated from ${relative(repoRoot, file)} by tools/css-module-types.ts. Do not edit.`,
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

export function writeDeclaration(file: string): string {
  const root = cssModulePackages
    .map((name) => join(repoRoot, name))
    .find((packageRoot) => file.startsWith(join(packageRoot, 'src') + sep));
  if (root === undefined) {
    throw new Error(`${file} is not in the src folder of ${cssModulePackages.join(' or ')}`);
  }
  const target = join(root, '.css-types', `${relative(root, file)}.d.ts`);
  mkdirSync(dirname(target), { recursive: true });
  // The type checks of the packages run in parallel and read each other's declarations: a reader sees the old file or
  // the new one, never none or half of one.
  const temporary = `${target}.${String(process.pid)}.tmp`;
  writeFileSync(temporary, declaration(file, readFileSync(file, 'utf8')));
  renameSync(temporary, target);
  return target;
}

function files(directory: string): string[] {
  if (!existsSync(directory)) {
    return [];
  }
  return readdirSync(directory, { withFileTypes: true }).flatMap((entry) =>
    entry.isDirectory() ? files(join(directory, entry.name)) : [join(directory, entry.name)],
  );
}

export function generateAll(packages: readonly string[] = cssModulePackages) {
  for (const name of packages) {
    const root = join(repoRoot, name);
    const written = new Set(modules(join(root, 'src')).map(writeDeclaration));
    // Only declarations of removed modules go; another process's temporary file is left to its rename.
    for (const stale of files(join(root, '.css-types'))) {
      if (!written.has(stale) && !stale.endsWith('.tmp')) {
        rmSync(stale);
      }
    }
  }
}

if (process.argv[1] === import.meta.filename) {
  const requested = process.argv.slice(2);
  const unknown = requested.filter((name) => !cssModulePackages.includes(name));
  if (unknown.length > 0) {
    throw new Error(
      `Unknown package ${unknown.join(', ')}; known: ${cssModulePackages.join(', ')}`,
    );
  }
  generateAll(requested.length === 0 ? cssModulePackages : requested);
}
