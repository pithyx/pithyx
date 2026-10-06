import type { Issue } from './issues';

export const MANIFEST_FILE = 'pithyx.json';
export const ICON_FILE = 'icon.svg';

/** Whether a package path stays inside the package wherever it is unpacked. */
export function isSafePath(path: string): boolean {
  return (
    path !== '' &&
    !path.startsWith('/') &&
    !path.includes('\\') &&
    // eslint-disable-next-line no-control-regex
    !/[\u0000-\u001f\u007f]/.test(path) &&
    path.split('/').every((segment) => segment !== '' && segment !== '.' && segment !== '..')
  );
}

/**
 * Checks the file paths of an app package (D301): `pithyx.json` and `icon.svg` at the root, the entry under
 * `frontend/`, files under `backend/` only with a backend, whose Compose file must be there. Paths are relative with
 * `/` as the separator; the list holds the regular files of the package only.
 */
export function checkLayout(
  paths: Iterable<string>,
  manifest: { entry: string; compose?: string | undefined },
): Issue[] {
  const issues: Issue[] = [];
  const issue = (path: string, reason: string): Issue => ({
    code: 'app.package.layout_invalid',
    params: { path, reason },
  });
  const seen = new Set<string>();
  for (const path of paths) {
    if (!isSafePath(path)) {
      issues.push(issue(path, 'unsafe'));
    } else if (seen.has(path)) {
      issues.push(issue(path, 'duplicate'));
    } else {
      seen.add(path);
      if (manifest.compose === undefined && path.startsWith('backend/')) {
        issues.push(issue(path, 'unexpected'));
      }
    }
  }
  for (const required of [MANIFEST_FILE, ICON_FILE, manifest.entry, manifest.compose]) {
    if (required !== undefined && !seen.has(required)) {
      issues.push(issue(required, 'missing'));
    }
  }
  return issues;
}
