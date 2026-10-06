#!/usr/bin/env node
// The pithyx command. For now only `pithyx validate <dir>` on an unpacked app (the built package layout); the other
// commands come with the developer tooling.
import { readdirSync, readFileSync } from 'node:fs';
import { join, relative, sep } from 'node:path';
import { validateCompose } from './compose';
import { validateIcon } from './icon';
import type { Issue } from './issues';
import { checkLayout, ICON_FILE, MANIFEST_FILE } from './layout';
import { validateManifest } from './manifest';

function files(root: string): string[] {
  return readdirSync(root, { recursive: true, withFileTypes: true })
    .filter((entry) => entry.isFile())
    .map((entry) => relative(root, join(entry.parentPath, entry.name)).split(sep).join('/'));
}

function validate(root: string): Issue[] {
  const paths = files(root);
  const issues: Issue[] = [];
  if (paths.includes(MANIFEST_FILE)) {
    const { issues: manifestIssues, manifest } = validateManifest(
      readFileSync(join(root, MANIFEST_FILE)),
    );
    issues.push(...manifestIssues);
    if (manifest !== undefined) {
      issues.push(
        ...checkLayout(paths, { entry: manifest.entry, compose: manifest.backend?.compose }),
      );
      const compose = manifest.backend?.compose;
      if (compose !== undefined && paths.includes(compose)) {
        issues.push(...validateCompose(readFileSync(join(root, compose)), manifest).issues);
      }
    }
  } else {
    issues.push({
      code: 'app.package.layout_invalid',
      params: { path: MANIFEST_FILE, reason: 'missing' },
    });
  }
  if (paths.includes(ICON_FILE)) {
    issues.push(...validateIcon(readFileSync(join(root, ICON_FILE))));
  }
  return issues;
}

const [command, directory] = process.argv.slice(2);
if (command !== 'validate' || directory === undefined) {
  console.error('Usage: pithyx validate <dir>');
  process.exit(2);
}
const issues = validate(directory);
for (const { code, params } of issues) {
  console.error(`${code} ${JSON.stringify(params)}`);
}
process.exit(issues.length === 0 ? 0 : 1);
