import type { Issue } from './issues';

export const MANIFEST_FILE = 'pithyx.json';
export const ICON_FILE = 'icon.svg';

const MAX_PATH_LENGTH = 512;
const MAX_SEGMENT_LENGTH = 255;
const MAX_DEPTH = 32;
/** The largest screenshot a manifest may name (D389). */
export const MAX_SCREENSHOT_BYTES = 1024 * 1024;
const SEGMENT = /^[A-Za-z0-9_~@+-][A-Za-z0-9._~@+-]*$/;

/**
 * Whether a package path stays inside the package wherever it is unpacked and names exactly one file: at most 32
 * segments of ASCII letters, digits and `._~@+-`, none empty or starting with a dot.
 */
export function isSafePath(path: string): boolean {
  if (path === '' || path.length > MAX_PATH_LENGTH) {
    return false;
  }
  const segments = path.split('/');
  return (
    segments.length <= MAX_DEPTH &&
    segments.every((segment) => segment.length <= MAX_SEGMENT_LENGTH && SEGMENT.test(segment))
  );
}

/** Whether a path below `frontend/` lies in the URLs of the backend (`api`, `api-<endpoint>`). */
export function isReservedFrontendPath(relative: string): boolean {
  const first = relative.split('/')[0] ?? '';
  return first === 'api' || first.startsWith('api-');
}

function placementProblem(path: string, hasBackend: boolean): string | null {
  const slash = path.indexOf('/');
  if (slash < 0) {
    return null;
  }
  switch (path.slice(0, slash)) {
    case 'frontend':
      return isReservedFrontendPath(path.slice(slash + 1)) ? 'reserved' : null;
    case 'backend':
      return hasBackend ? null : 'unexpected';
    default:
      return 'unexpected';
  }
}

/**
 * Checks the file paths of an app package (D301): `pithyx.json` and `icon.svg` at the root (next to README, LICENSE
 * and checksums), the entry under `frontend/`, files under `backend/` only with a backend, whose Compose file must be
 * there. Paths are relative with `/` as the separator and use ASCII letters, digits and `._~@+-` only, so no two names
 * can mean the same file on any file system; the list holds the regular files of the package only. `frontend/api`
 * and `frontend/api-*` are reserved: their URLs belong to the backend (D303). The manifest's screenshots must be files of
 * the package of at most 1 MiB each (D389).
 */
export function checkLayout(
  paths: Iterable<string>,
  manifest: {
    entry: string;
    compose?: string | undefined;
    screenshots?: readonly string[] | undefined;
  },
  sizeOf: (path: string) => number = () => 0,
): Issue[] {
  const issues: Issue[] = [];
  const issue = (path: string, reason: string): Issue => ({
    code: 'app.package.layout_invalid',
    params: { path, reason },
  });
  const seen = new Set<string>();
  // Two names that differ only in case are one file on a case-insensitive client, and a file is in the way of a
  // folder of the same name.
  const folded = new Map<string, string>();
  const files: string[] = [];
  for (const path of paths) {
    if (!isSafePath(path)) {
      issues.push(issue(path, 'unsafe'));
    } else if (seen.has(path) || folded.has(path.toLowerCase())) {
      issues.push(issue(path, 'duplicate'));
    } else {
      seen.add(path);
      folded.set(path.toLowerCase(), path);
      files.push(path);
      const problem = placementProblem(path, manifest.compose !== undefined);
      if (problem !== null) {
        issues.push(issue(path, problem));
      }
    }
  }
  const inTheWay = new Set<string>();
  for (const path of files) {
    for (let slash = path.indexOf('/'); slash > 0; slash = path.indexOf('/', slash + 1)) {
      const file = folded.get(path.slice(0, slash).toLowerCase());
      if (file !== undefined && !inTheWay.has(file)) {
        inTheWay.add(file);
        issues.push(issue(file, 'duplicate'));
      }
    }
  }
  for (const required of [MANIFEST_FILE, ICON_FILE, manifest.entry, manifest.compose]) {
    if (required !== undefined && !seen.has(required)) {
      issues.push(issue(required, 'missing'));
    }
  }
  for (const screenshot of new Set(manifest.screenshots ?? [])) {
    if (!seen.has(screenshot)) {
      issues.push(issue(screenshot, 'missing'));
    } else if (sizeOf(screenshot) > MAX_SCREENSHOT_BYTES) {
      issues.push(issue(screenshot, 'too_large'));
    }
  }
  return issues;
}
