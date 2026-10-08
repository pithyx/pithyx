// Raises the SDK version in the template repositories (A9): every `@pithyx/*` dependency of app-blank, app-chat and
// app-showcase to the new version, exactly, and the manifest's `sdk` range to `^<version>`. Run it after each publish
// with tools/release/publish-sdk.sh, against local clones of the templates:
//
//   node tools/release/bump-templates.ts <folder with the clones> <version> [--install]
//
// --install also runs `npm install` in each clone, which needs the version on the registry and rewrites
// package-lock.json. The script changes files only; reviewing, testing (`npm test`), committing and pushing stay with
// the owner, like the publish itself (D306).
import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

const TEMPLATES = ['app-blank', 'app-chat', 'app-showcase'];
const VERSION = /^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/;

/** The package.json text with every `@pithyx/*` dependency at the version; the same text when nothing changes. */
export function bumpPackage(text: string, version: string): string {
  const json = JSON.parse(text) as Record<string, unknown>;
  for (const field of ['dependencies', 'devDependencies', 'peerDependencies']) {
    const dependencies = json[field] as Record<string, string> | undefined;
    for (const name of Object.keys(dependencies ?? {})) {
      if (name.startsWith('@pithyx/') && dependencies !== undefined) {
        dependencies[name] = version;
      }
    }
  }
  return `${JSON.stringify(json, null, 2)}\n`;
}

/** The pithyx.json text with `sdk` at `^<version>`. */
export function bumpManifest(text: string, version: string): string {
  const json = JSON.parse(text) as Record<string, unknown>;
  json.sdk = `^${version}`;
  return `${JSON.stringify(json, null, 2)}\n`;
}

function main(args: string[]): void {
  const install = args.includes('--install');
  const [folder, version] = args.filter((arg) => arg !== '--install');
  if (folder === undefined || version === undefined || !VERSION.test(version)) {
    console.error(
      'usage: node tools/release/bump-templates.ts <folder with the clones> <version> [--install]',
    );
    process.exit(2);
  }
  for (const template of TEMPLATES) {
    const root = join(folder, template);
    if (!existsSync(join(root, 'package.json')) || !existsSync(join(root, 'pithyx.json'))) {
      console.error(`bump-templates: ${root} is not a template clone`);
      process.exit(1);
    }
    for (const [file, bump] of [
      ['package.json', bumpPackage],
      ['pithyx.json', bumpManifest],
    ] as const) {
      const path = join(root, file);
      writeFileSync(path, bump(readFileSync(path, 'utf8'), version));
    }
    if (install) {
      execFileSync('npm', ['install'], { cwd: root, stdio: 'inherit' });
    }
    console.log(`bump-templates: ${template} now uses @pithyx/* ${version}`);
  }
}

if (import.meta.url === `file://${process.argv[1] ?? ''}`) {
  main(process.argv.slice(2));
}
