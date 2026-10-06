// Writes a declaration with the exact class names of every CSS Module under <package>/src into <package>/.css-types/
// (gitignored). Each tsconfig maps .css-types/src onto src through rootDirs, so `styles.name` is a plain string and a
// typo is a type error. Runs before typecheck and lint, and inside the desktop's Vite dev server on every change.
//
//   node tools/css-module-types.ts [<package> ...]   every package below when none is given
import { mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
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

export function writeDeclaration(file: string) {
  const root = cssModulePackages
    .map((name) => join(repoRoot, name))
    .find((packageRoot) => file.startsWith(join(packageRoot, 'src') + sep));
  if (root === undefined) {
    throw new Error(`${file} is not in the src folder of ${cssModulePackages.join(' or ')}`);
  }
  const target = join(root, '.css-types', `${relative(root, file)}.d.ts`);
  mkdirSync(dirname(target), { recursive: true });
  writeFileSync(target, declaration(file, readFileSync(file, 'utf8')));
}

export function generateAll(packages: readonly string[] = cssModulePackages) {
  for (const name of packages) {
    const root = join(repoRoot, name);
    rmSync(join(root, '.css-types'), { recursive: true, force: true });
    for (const file of modules(join(root, 'src'))) {
      writeDeclaration(file);
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
