import rules from '@pithyx/sdk/schema/catalog.v1.rules.json' with { type: 'json' };
import maintainersSchema from '@pithyx/sdk/schema/maintainers.v1.schema.json' with { type: 'json' };
import { mayUseReservedIds } from './catalog';
import { devComposePath, validateCompose } from './compose';
import { unpinnedImages } from './dockerfile';
import { validateIcon } from './icon';
import type { Issue } from './issues';
import { compareOrdinal, compileSchema, parseStrictJson } from './json-schema';
import { validateManifest } from './manifest';
import { compareVersions } from './semver';
import { parseSpdxExpression } from './spdx';

/** The rules of `@pithyx/sdk/schema/catalog.v1.rules.json`, as both the CLI and the box read them. */
export const CATALOG_RULES = rules;

/** A pull request to a catalog repository, as the checks see it. */
export interface CatalogPullRequest {
  /** The `id` of the repository's `catalog.json`. */
  catalog: string;
  /** The GitHub login of the pull request's author. */
  author: string;
  /** Every path the pull request adds, changes or deletes, relative to the repository root. */
  changed: readonly string[];
  /** The files of the app's folder after the pull request, by path relative to the repository root. */
  files: ReadonlyMap<string, Uint8Array>;
  /** The versions of the app the catalog has published, empty for a new app. */
  published: readonly string[];
  /** The app's `maintainers.json` before the pull request; undefined for a new app. */
  maintainers?: readonly string[] | undefined;
}

const validateMaintainers = compileSchema(maintainersSchema);
const dockerfile = new RegExp(rules.dockerfile);
const minified = new RegExp(rules.minified);
const screenshotName = new RegExp(rules.screenshots.name);
const osi = new Set(rules.licenses.osi.map((id) => id.toLowerCase()));
const exceptions = new Set(rules.licenses.exceptions.map((id) => id.toLowerCase()));
const media = rules.media as Record<
  string,
  { type: string; magic: { offset: number; hex: string }[] }
>;

function violation(rule: string, params: Record<string, string | number>): Issue {
  return { code: 'store.catalog.rule_violation', params: { rule, ...params } };
}

function basename(path: string): string {
  return path.slice(path.lastIndexOf('/') + 1);
}

function extension(name: string): string {
  const dot = name.lastIndexOf('.');
  return dot <= 0 ? '' : name.slice(dot + 1).toLowerCase();
}

function startsWithMagic(bytes: Uint8Array, magic: { offset: number; hex: string }[]): boolean {
  return magic.every(({ offset, hex }) => {
    const expected = Buffer.from(hex, 'hex');
    return (
      bytes.length >= offset + expected.length &&
      expected.every((byte, index) => bytes[offset + index] === byte)
    );
  });
}

/**
 * Checks a pull request to a catalog repository against `catalog.v1.rules.json`, with the same verdicts and codes as
 * the box (the corpus in `@pithyx/sdk/schema/fixtures/catalog/rules` holds both alike): one app folder, the folder
 * equal to the app's id, the required files, only text and media files of the allowed kinds, no binaries or minified
 * code, every image of a Dockerfile pinned, at most 5 MB, an OSI license, the required manifest fields, a version
 * above every published one, a valid manifest, icon and `compose.dev.yaml`, and an author from `maintainers.json`.
 */
export function checkCatalogPullRequest(request: CatalogPullRequest): Issue[] {
  const issues: Issue[] = [];
  const prefix = `${rules.appsFolder}/`;
  const folders = new Set<string>();
  for (const path of [...request.changed].sort(compareOrdinal)) {
    const [folder = '', ...rest] = path.startsWith(prefix)
      ? path.slice(prefix.length).split('/')
      : [];
    if (folder === '' || rest.length === 0) {
      issues.push(violation('one_app', { path }));
    } else {
      folders.add(folder);
    }
  }
  const [folder] = folders;
  if (folder === undefined || folders.size > 1) {
    for (const each of folders) {
      issues.push(violation('one_app', { path: `${prefix}${each}` }));
    }
    return issues;
  }
  const root = `${prefix}${folder}/`;

  const tree = new Map(
    [...request.files.entries()]
      .filter(([path]) => path.startsWith(root))
      .map(([path, bytes]) => [path.slice(root.length), bytes] as const)
      .sort(([a], [b]) => compareOrdinal(a, b)),
  );
  for (const alternatives of rules.requiredFiles) {
    if (!alternatives.some((name) => tree.has(name))) {
      issues.push(violation('required_file', { path: `${root}${alternatives[0] ?? ''}` }));
    }
  }

  let size = 0;
  let screenshots = 0;
  for (const [relative, bytes] of tree) {
    size += bytes.length;
    issues.push(...fileIssues(`${root}${relative}`, relative, bytes));
    if (relative.startsWith(`${rules.screenshots.folder}/`)) {
      screenshots++;
      const name = relative.slice(rules.screenshots.folder.length + 1);
      if (!screenshotName.test(name) || bytes.length > rules.limits.screenshotBytes) {
        issues.push(violation('screenshot', { path: `${root}${relative}` }));
      }
    }
  }
  if (screenshots > rules.limits.screenshots) {
    issues.push(
      violation('screenshots', {
        path: `${root}${rules.screenshots.folder}`,
        limit: rules.limits.screenshots,
      }),
    );
  }
  if (size > rules.limits.appBytes) {
    issues.push(
      violation('app_size', { path: root.slice(0, -1), size, limit: rules.limits.appBytes }),
    );
  }

  const manifestBytes = tree.get('pithyx.json');
  if (manifestBytes !== undefined) {
    issues.push(...manifestIssues(request, root, folder, manifestBytes, tree));
  }
  const icon = tree.get('icon.svg');
  if (icon !== undefined) {
    issues.push(...validateIcon(icon));
  }
  issues.push(...maintainerIssues(request, root, tree.get('maintainers.json')));
  return issues;
}

function fileIssues(path: string, relative: string, bytes: Uint8Array): Issue[] {
  const name = basename(relative);
  const segments = relative.split('/');
  const hidden = segments.some(
    (segment, index) =>
      segment.startsWith('.') &&
      !(index === segments.length - 1 && rules.text.names.includes(name)),
  );
  if (hidden) {
    return [violation('file_type', { path })];
  }
  const ext = extension(name);
  if (
    dockerfile.test(name) ||
    rules.text.names.includes(name) ||
    rules.text.extensions.includes(ext)
  ) {
    return textIssues(path, name, ext, bytes);
  }
  const kind = media[ext];
  if (kind !== undefined) {
    return startsWithMagic(bytes, kind.magic) ? [] : [violation('media', { path })];
  }
  return [violation('file_type', { path })];
}

function textIssues(path: string, name: string, ext: string, bytes: Uint8Array): Issue[] {
  let text: string;
  try {
    text = new TextDecoder('utf-8', { fatal: true }).decode(bytes);
  } catch {
    return [violation('binary', { path })];
  }
  if (text.includes('\0')) {
    return [violation('binary', { path })];
  }
  if (minified.test(name)) {
    return [violation('minified', { path })];
  }
  const issues: Issue[] = [];
  if (!rules.text.longLines.includes(ext)) {
    const long = text.split('\n').findIndex((line) => line.length > rules.limits.lineChars);
    if (long >= 0) {
      issues.push(violation('minified', { path, line: long + 1 }));
    }
  }
  if (dockerfile.test(name)) {
    for (const { line, image } of unpinnedImages(text)) {
      issues.push(violation('unpinned', { path, line, image }));
    }
  }
  return issues;
}

function licenseAccepted(expression: string): boolean {
  const parsed = parseSpdxExpression(expression);
  return (
    parsed !== undefined &&
    parsed.licenses.every((license) => osi.has(license.replace(/\+$/, '').toLowerCase())) &&
    parsed.exceptions.every((exception) => exceptions.has(exception.toLowerCase()))
  );
}

function manifestIssues(
  request: CatalogPullRequest,
  root: string,
  folder: string,
  bytes: Uint8Array,
  tree: ReadonlyMap<string, Uint8Array>,
): Issue[] {
  const path = `${root}pithyx.json`;
  const result = validateManifest(bytes, { allowReservedId: mayUseReservedIds(request.catalog) });
  const manifest = result.manifest;
  if (manifest === undefined) {
    return result.issues;
  }
  const issues: Issue[] = [];
  if (manifest.id !== folder) {
    issues.push(violation('folder_id', { path, id: manifest.id }));
  }
  for (const field of rules.manifestFields) {
    if (!(field in manifest)) {
      issues.push(violation('manifest_field', { path, field }));
    }
  }
  if (manifest.license !== undefined && !licenseAccepted(manifest.license)) {
    issues.push(violation('license', { path, license: manifest.license }));
  }
  const newest = [...request.published].sort(compareVersions).at(-1);
  if (newest !== undefined && compareVersions(manifest.version, newest) <= 0) {
    issues.push(
      violation('version_not_higher', { path, version: manifest.version, published: newest }),
    );
  }
  if (manifest.backend !== undefined) {
    const compose = devComposePath(manifest.backend.compose);
    const source = tree.get(compose);
    if (source === undefined) {
      issues.push(violation('required_file', { path: `${root}${compose}` }));
    } else {
      issues.push(...validateCompose(source, manifest, { dev: true }).issues);
    }
  }
  return issues;
}

function maintainerIssues(
  request: CatalogPullRequest,
  root: string,
  bytes: Uint8Array | undefined,
): Issue[] {
  const listed = (logins: readonly string[]) =>
    logins.some((login) => login.toLowerCase() === request.author.toLowerCase());
  const issues: Issue[] = [];
  if (request.maintainers !== undefined && !listed(request.maintainers)) {
    issues.push(violation('maintainer', { author: request.author }));
  }
  if (bytes !== undefined) {
    const parsed = parseStrictJson(bytes);
    if (parsed === undefined || !validateMaintainers(parsed.value)) {
      issues.push(violation('maintainers_invalid', { path: `${root}maintainers.json` }));
    } else if (
      request.maintainers === undefined &&
      !listed((parsed.value as { maintainers: string[] }).maintainers)
    ) {
      // A new app's own file must name its author, who becomes its first maintainer.
      issues.push(violation('maintainer', { author: request.author }));
    }
  }
  return issues;
}
