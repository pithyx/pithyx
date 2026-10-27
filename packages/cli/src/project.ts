import { createHash } from 'node:crypto';
import { existsSync, lstatSync, readdirSync, readFileSync } from 'node:fs';
import { dirname, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import type { AppManifest } from '@pithyx/sdk';
import { devComposePath, processHealthchecks, validateCompose } from './compose';
import { error, type Finding } from './findings';
import { validateIcon } from './icon';
import { checkLayout, ICON_FILE, MANIFEST_FILE } from './layout';
import { ARCHITECTURES, composeImages, LOCK_FILE, lockDifferences, parseLock } from './lock';
import { validateManifest } from './manifest';

/** The file with the SHA-256 of every other file of a package, in the format of `sha256sum` (D315). */
export const CHECKSUMS_FILE = 'CHECKSUMS';

/** Files at the project root that go into the package as they are, when they exist. */
export const EXTRA_FILES = ['README.md', 'README', 'LICENSE', 'LICENSE.md', 'LICENSE.txt'];

/** The default folder of the built frontend, Vite's `build.outDir`. */
export const DEFAULT_FRONTEND = 'dist';

/** A file of the package and where its bytes come from. */
export interface PackageEntry {
  path: string;
  /** The file on disk, or the bytes for a generated file. */
  source: string | Uint8Array;
}

/** An app project on the developer's machine: `pithyx.json` and `icon.svg` at its root, the built frontend in a folder. */
export interface Project {
  root: string;
  /** The built frontend, absolute. */
  frontend: string;
  /** The manifest when it is valid. */
  manifest?: AppManifest;
  /** Every file the package gets, `CHECKSUMS` left out; in package order. */
  entries: PackageEntry[];
  findings: Finding[];
}

export interface ProjectOptions {
  /** The built frontend, relative to the project root; default `dist`. */
  frontend?: string | undefined;
  /**
   * A backend that has only its source form `compose.dev.yaml` yet is a warning instead of an error, and the source
   * form is checked instead: `vite build` of an app in a catalog runs before the catalog builds the images and writes
   * `compose.yaml` (D330), and `pithyx validate` works before `pithyx build`, which needs Docker.
   */
  allowUnbuiltBackend?: boolean | undefined;
  /**
   * The id may be in `org.pithyx.*`, which a box refuses for a package from a file: for the apps of the official
   * catalog, whose CI packs them (D381). Every other check stays.
   */
  allowReservedId?: boolean | undefined;
}

/** The regular files below a folder; hidden ones go to `skipped`, links and special files to `refused`. */
function frontendFiles(folder: string, skipped: string[], refused: string[]): string[] {
  const files: string[] = [];
  const walk = (directory: string) => {
    for (const entry of readdirSync(directory, { withFileTypes: true })) {
      const path = join(directory, entry.name);
      const relativePath = relative(folder, path).split(sep).join('/');
      if (entry.name.startsWith('.')) {
        skipped.push(relativePath);
      } else if (entry.isDirectory()) {
        walk(path);
      } else if (entry.isFile()) {
        files.push(relativePath);
      } else {
        refused.push(relativePath);
      }
    }
  };
  walk(folder);
  return files;
}

function bytes(entry: PackageEntry): Uint8Array {
  return typeof entry.source === 'string' ? readFileSync(entry.source) : entry.source;
}

/** Package order: the manifest, the icon, then every other path sorted by code unit (D315). */
export function packageOrder(a: string, b: string): number {
  const rank = (path: string) => (path === MANIFEST_FILE ? 0 : path === ICON_FILE ? 1 : 2);
  return rank(a) - rank(b) || (a < b ? -1 : a > b ? 1 : 0);
}

/** The template icon of new apps, shipped with the package (`@pithyx/cli/template-icon.svg`). */
export const TEMPLATE_ICON_PATH = join(
  dirname(fileURLToPath(import.meta.url)),
  '..',
  'template-icon.svg',
);

let templateIconDigest: string | undefined;

function normalizeText(data: Uint8Array): string {
  return new TextDecoder().decode(data).replace(/\r\n/g, '\n').trim();
}

/** Whether an icon is the template icon (the Pithyx box on a neutral tile), up to line endings and outer whitespace. */
export function isTemplateIcon(icon: Uint8Array): boolean {
  templateIconDigest ??= createHash('sha256')
    .update(normalizeText(readFileSync(TEMPLATE_ICON_PATH)))
    .digest('hex');
  return createHash('sha256').update(normalizeText(icon)).digest('hex') === templateIconDigest;
}

/**
 * Reads a project and checks it as the box would check the package built from it: the manifest, the icon, the
 * layout with the entry in the built frontend, the Compose file and the images lock. Warns while the icon is still the
 * template's, and about hidden files of the build, which stay out of the package.
 */
export function readProject(root: string, options: ProjectOptions = {}): Project {
  const projectRoot = resolve(root);
  const frontend = resolve(projectRoot, options.frontend ?? DEFAULT_FRONTEND);
  const findings: Finding[] = [];
  const entries: PackageEntry[] = [];
  const at = (path: string) => join(projectRoot, ...path.split('/'));

  for (const path of [MANIFEST_FILE, ICON_FILE, ...EXTRA_FILES]) {
    if (existsSync(at(path)) && lstatSync(at(path)).isFile()) {
      entries.push({ path, source: at(path) });
    }
  }

  let manifest: AppManifest | undefined;
  const manifestEntry = entries.find((entry) => entry.path === MANIFEST_FILE);
  if (manifestEntry !== undefined) {
    const validation = validateManifest(bytes(manifestEntry), {
      allowReservedId: options.allowReservedId === true,
    });
    findings.push(...validation.issues.map(error));
    manifest = validation.manifest;
    if (manifest !== undefined && 'docker.socket.raw' in manifest.permissions) {
      findings.push({ level: 'warning', code: 'cli.rights.raw_socket', params: {} });
    }
  } else {
    findings.push({
      level: 'error',
      code: 'cli.manifest.missing',
      params: { folder: projectRoot },
    });
  }

  const iconEntry = entries.find((entry) => entry.path === ICON_FILE);
  if (iconEntry !== undefined) {
    const icon = bytes(iconEntry);
    findings.push(...validateIcon(icon).map(error));
    if (isTemplateIcon(icon)) {
      findings.push({ level: 'warning', code: 'cli.icon.template', params: {} });
    }
  }

  if (existsSync(frontend) && lstatSync(frontend).isDirectory()) {
    const skipped: string[] = [];
    const refused: string[] = [];
    for (const path of frontendFiles(frontend, skipped, refused)) {
      entries.push({ path: `frontend/${path}`, source: join(frontend, ...path.split('/')) });
    }
    const shown = (path: string) =>
      `${relative(projectRoot, frontend).split(sep).join('/')}/${path}`;
    for (const path of skipped) {
      findings.push({
        level: 'warning',
        code: 'cli.frontend.skipped',
        params: { path: shown(path) },
      });
    }
    for (const path of refused) {
      findings.push({
        level: 'error',
        code: 'app.package.invalid',
        params: { reason: 'entry_type', path: shown(path) },
      });
    }
  } else {
    findings.push({
      level: 'error',
      code: 'cli.frontend.missing',
      params: { folder: relative(projectRoot, frontend) || '.' },
    });
  }

  const declared = manifest?.backend?.compose;
  const unbuilt =
    options.allowUnbuiltBackend === true &&
    declared !== undefined &&
    !existsSync(at(declared)) &&
    existsSync(at(devComposePath(declared)));
  if (unbuilt) {
    findings.push({ level: 'warning', code: 'cli.backend.unbuilt', params: { path: declared } });
    // The source form is still checked, with build in place of image.
    if (manifest !== undefined && lstatSync(at(devComposePath(declared))).isFile()) {
      const source = readFileSync(at(devComposePath(declared)));
      findings.push(...validateCompose(source, manifest, { dev: true }).issues.map(error));
    }
  }
  const compose = unbuilt ? undefined : declared;
  if (compose !== undefined) {
    for (const path of [compose, LOCK_FILE]) {
      if (existsSync(at(path)) && lstatSync(at(path)).isFile()) {
        entries.push({ path, source: at(path) });
      }
    }
  }

  entries.sort((a, b) => packageOrder(a.path, b.path));

  if (manifest !== undefined) {
    findings.push(
      ...checkLayout(
        entries.map((entry) => entry.path),
        { entry: manifest.entry, compose },
      ).map(error),
    );
  }
  if (manifest !== undefined && compose !== undefined) {
    findings.push(...checkBackend(manifest, compose, entries));
  }
  return { root: projectRoot, frontend, entries, findings, ...(manifest && { manifest }) };
}

/** What `pithyx lock` finds in a project: an invalid manifest (with the errors), no backend, or the Compose file to lock. */
export function lockTarget(
  project: Project,
): { kind: 'invalid'; findings: Finding[] } | { kind: 'none' } | { kind: 'compose'; path: string } {
  if (project.manifest === undefined) {
    return {
      kind: 'invalid',
      findings: project.findings.filter((finding) => finding.level === 'error'),
    };
  }
  const path = project.manifest.backend?.compose;
  return path === undefined ? { kind: 'none' } : { kind: 'compose', path };
}

function checkBackend(manifest: AppManifest, compose: string, entries: PackageEntry[]): Finding[] {
  const composeEntry = entries.find((entry) => entry.path === compose);
  if (composeEntry === undefined) {
    return [];
  }
  const composeBytes = bytes(composeEntry);
  const issues = validateCompose(composeBytes, manifest).issues;
  if (issues.length > 0) {
    return issues.map(error);
  }
  const lockEntry = entries.find((entry) => entry.path === LOCK_FILE);
  if (lockEntry === undefined) {
    return [{ level: 'error', code: 'cli.lock.missing', params: { path: LOCK_FILE } }];
  }
  const lock = parseLock(bytes(lockEntry));
  if (lock === null) {
    return [{ level: 'error', code: 'cli.lock.invalid', params: { path: LOCK_FILE } }];
  }
  const { missing, unused } = lockDifferences(lock, composeImages(composeBytes));
  const findings: Finding[] = [
    ...missing.map((image): Finding => ({
      level: 'error',
      code: 'cli.lock.stale',
      params: { path: LOCK_FILE, reason: 'missing', image },
    })),
    ...unused.map((image): Finding => ({
      level: 'error',
      code: 'cli.lock.stale',
      params: { path: LOCK_FILE, reason: 'unused', image },
    })),
  ];
  for (const [image, platforms] of Object.entries(lock.images)) {
    for (const arch of ARCHITECTURES) {
      if (platforms[arch] === undefined) {
        findings.push({
          level: 'warning',
          code: 'cli.lock.platform_missing',
          params: { image, arch },
        });
      }
    }
  }
  if (manifest.backend?.health !== undefined) {
    for (const service of processHealthchecks(composeBytes)) {
      findings.push({
        level: 'warning',
        code: 'cli.compose.healthcheck_redundant',
        params: { service },
      });
    }
  }
  return findings;
}

/** The text of `CHECKSUMS`: `<sha256>  <path>` for every other file, in path order, as `sha256sum -c` reads it. */
export function checksums(entries: readonly PackageEntry[]): Uint8Array {
  const lines = entries
    .filter((entry) => entry.path !== CHECKSUMS_FILE)
    .sort((a, b) => (a.path < b.path ? -1 : a.path > b.path ? 1 : 0))
    .map((entry) => `${createHash('sha256').update(bytes(entry)).digest('hex')}  ${entry.path}\n`);
  return new TextEncoder().encode(lines.join(''));
}

export { bytes as entryBytes };
