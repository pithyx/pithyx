import { execFileSync } from 'node:child_process';
import { createHash, createPrivateKey, createPublicKey, sign, type KeyObject } from 'node:crypto';
import {
  copyFileSync,
  existsSync,
  lstatSync,
  mkdirSync,
  readdirSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from 'node:fs';
import { join, relative, sep } from 'node:path';
import rules from '@pithyx/sdk/schema/catalog.v1.rules.json' with { type: 'json' };
import {
  checkSignedIndex,
  validateCatalog,
  validateIndex,
  type CatalogChannel,
  type CatalogFile,
  type IndexFile,
  type TrustedKey,
} from './catalog';
import { checkCatalogPullRequest } from './catalog-rules';
import type { Issue } from './issues';
import { ARCHITECTURES, LOCK_FILE, parseLock } from './lock';
import { validateManifest } from './manifest';
import { packageName } from './pack';
import { compareVersions } from './semver';
import { readTarGz } from './untar';

export const INDEX_FILE = 'index.json';
export const SIGNATURE_FILE = 'index.json.sig';
export const CATALOG_FILE = 'catalog.json';

/** The branch of a channel: `catalog` for `stable`, `catalog-next` for `beta`. */
export const CHANNEL_BRANCHES: Record<CatalogChannel, string> = {
  stable: 'catalog',
  beta: 'catalog-next',
};

function sha256(bytes: Uint8Array): string {
  return createHash('sha256').update(bytes).digest('hex');
}

function fileEntry(bytes: Uint8Array): { sha256: string; size: number } {
  return { sha256: sha256(bytes), size: bytes.length };
}

function git(root: string, args: readonly string[]): string {
  return execFileSync('git', ['-C', root, ...args], {
    encoding: 'utf8',
    maxBuffer: 64 * 1024 * 1024,
  });
}

function gitBytes(root: string, args: readonly string[]): Buffer | undefined {
  try {
    return execFileSync('git', ['-C', root, ...args], {
      maxBuffer: 64 * 1024 * 1024,
      stdio: ['ignore', 'pipe', 'ignore'],
    });
  } catch {
    return undefined;
  }
}

function filesBelow(folder: string): string[] {
  if (!existsSync(folder)) {
    return [];
  }
  return readdirSync(folder, { recursive: true, encoding: 'utf8' })
    .filter((path) => lstatSync(join(folder, path)).isFile())
    .map((path) => path.split(sep).join('/'))
    .sort();
}

/** Reads `index.json` of a branch folder as it is, or undefined when the branch has none yet. */
export function readIndex(branch: string): { bytes: Buffer; index: IndexFile } | undefined {
  const path = join(branch, INDEX_FILE);
  if (!existsSync(path)) {
    return undefined;
  }
  const bytes = readFileSync(path);
  return { bytes, index: JSON.parse(bytes.toString('utf8')) as IndexFile };
}

/** Reads and checks `catalog.json`; throws with its issues. */
export function readCatalog(
  path: string,
  options: { allowReservedId?: boolean } = {},
): CatalogFile {
  const result = validateCatalog(readFileSync(path), options);
  if (result.catalog === undefined) {
    throw new IssuesError(`${path} is not a valid catalog.json`, result.issues);
  }
  return result.catalog;
}

/** Problems found by a catalog command, with the issues to print. */
export class IssuesError extends Error {
  constructor(
    message: string,
    readonly issues: readonly Issue[],
  ) {
    super(message);
  }
}

export interface CheckRequest {
  /** The repository's checkout at the pull request's head. */
  root: string;
  /** The commit the pull request is based on. */
  base: string;
  /** The GitHub login of the pull request's author. */
  author: string;
  /** `index.json` of `catalog-next`, which names the published versions; absent before the first publish. */
  index?: string | undefined;
  allowReservedId?: boolean;
}

/**
 * `pithyx catalog check`: the pull request rules of `catalog.v1.rules.json` on the changes between the base and the
 * checkout, as the catalog's CI runs them, plus `catalog.json`. Returns the issues, empty when the pull request passes.
 */
export function checkPullRequest(request: CheckRequest): Issue[] {
  const catalog = readCatalog(join(request.root, CATALOG_FILE), {
    allowReservedId: request.allowReservedId ?? false,
  });
  const changed = git(request.root, [
    'diff',
    '--name-only',
    '--no-renames',
    `${request.base}...HEAD`,
  ])
    .split('\n')
    .filter((line) => line !== '');
  const prefix = `${rules.appsFolder}/`;
  const folders = new Set(
    changed.filter((path) => path.startsWith(prefix)).map((path) => path.split('/')[1] ?? ''),
  );
  const files = new Map<string, Uint8Array>();
  for (const folder of folders) {
    const root = join(request.root, rules.appsFolder, folder);
    for (const path of filesBelow(root)) {
      files.set(`${prefix}${folder}/${path}`, readFileSync(join(root, path)));
    }
  }
  const [only] = folders;
  const published =
    only !== undefined && request.index !== undefined && existsSync(request.index)
      ? Object.keys(
          (JSON.parse(readFileSync(request.index, 'utf8')) as IndexFile).apps[only]?.versions ?? {},
        )
      : [];
  const before =
    only === undefined
      ? undefined
      : gitBytes(request.root, ['show', `${request.base}:${prefix}${only}/maintainers.json`]);
  let maintainers: string[] | undefined;
  if (before !== undefined) {
    try {
      maintainers = (JSON.parse(before.toString('utf8')) as { maintainers?: string[] }).maintainers;
    } catch {
      maintainers = [];
    }
  }
  return checkCatalogPullRequest({
    catalog: catalog.id,
    author: request.author,
    changed,
    files,
    published,
    maintainers,
  });
}

/** An app version of `master` that `catalog-next` does not have yet. */
export interface PendingVersion {
  id: string;
  version: string;
  /** The app's folder relative to the repository root. */
  path: string;
  backend: boolean;
}

/** `pithyx catalog pending`: the app versions on `master` that the index of `catalog-next` does not list. */
export function pendingVersions(root: string, index?: string): PendingVersion[] {
  const published =
    index !== undefined && existsSync(index)
      ? (JSON.parse(readFileSync(index, 'utf8')) as IndexFile).apps
      : {};
  const apps = join(root, rules.appsFolder);
  if (!existsSync(apps)) {
    return [];
  }
  const pending: PendingVersion[] = [];
  for (const folder of readdirSync(apps).sort()) {
    const manifestPath = join(apps, folder, 'pithyx.json');
    if (!existsSync(manifestPath)) {
      continue;
    }
    const manifest = JSON.parse(readFileSync(manifestPath, 'utf8')) as {
      id: string;
      version: string;
      backend?: unknown;
    };
    if (published[manifest.id]?.versions[manifest.version] === undefined) {
      pending.push({
        id: manifest.id,
        version: manifest.version,
        path: `${rules.appsFolder}/${folder}`,
        backend: manifest.backend !== undefined,
      });
    }
  }
  return pending;
}

/** What a new index is built from. */
export interface IndexRequest {
  /** The branch folder (`catalog-next` or `catalog`), written in place. */
  branch: string;
  catalog: CatalogFile;
  channel: CatalogChannel;
  /** Packages to add, built by CI from `source`. */
  add?: readonly string[] | undefined;
  /** The checkout of `master` the packages were built from; screenshots come from its app folders. */
  source?: string | undefined;
  /** The repository's URL and the commit of `master`, for the index's `source`. */
  repository?: string | undefined;
  commit?: string | undefined;
  /** Another branch folder whose versions are copied with their entries (promote `catalog-next` to `catalog`). */
  promote?: string | undefined;
  now?: Date | undefined;
}

type VersionEntry = IndexFile['apps'][string]['versions'][string] & {
  publishedAt: string;
  package: { sha256: string; size: number };
  manifest: { sha256: string; size: number };
  icon: { sha256: string; size: number };
  screenshots?: { name: string; sha256: string; size: number }[];
  pithyx: string;
  license: string;
};

function timestamp(date: Date): string {
  return `${date.toISOString().slice(0, 19)}Z`;
}

/** Throws unless every file of a version folder matches its index entry. */
function verifyVersionFiles(folder: string, id: string, version: string, entry: VersionEntry) {
  const expected: [string, { sha256: string; size: number }][] = [
    [packageName({ id, version }), entry.package],
    ['pithyx.json', entry.manifest],
    ['icon.svg', entry.icon],
    ...(entry.screenshots ?? []).map((shot): [string, { sha256: string; size: number }] => [
      `screenshots/${shot.name}`,
      shot,
    ]),
  ];
  const issues: Issue[] = [];
  for (const [name, file] of expected) {
    const path = join(folder, name);
    const bytes = existsSync(path) ? readFileSync(path) : undefined;
    if (bytes?.length !== file.size || sha256(bytes) !== file.sha256) {
      issues.push({
        code: 'store.package.hash_mismatch',
        params: { path: `apps/${id}/${version}/${name}` },
      });
    }
  }
  if (issues.length > 0) {
    throw new IssuesError(`apps/${id}/${version} does not match the index`, issues);
  }
}

/** Puts a built package into the branch as `apps/<id>/<version>/` and returns its index entry. */
function addPackage(
  request: IndexRequest,
  packagePath: string,
  now: Date,
): [string, string, VersionEntry] {
  const files = readTarGz(packagePath);
  const manifestBytes = files.get('pithyx.json');
  const icon = files.get('icon.svg');
  if (manifestBytes === undefined || icon === undefined) {
    throw new Error(`${packagePath} has no pithyx.json or icon.svg`);
  }
  const checked = validateManifest(manifestBytes, {
    allowReservedId: rules.reservedIds.catalogs.includes(request.catalog.id),
  });
  const manifest = checked.manifest;
  if (manifest === undefined) {
    throw new IssuesError(`${packagePath} has an invalid pithyx.json`, checked.issues);
  }
  const { id, version } = manifest;
  const folder = join(request.branch, rules.appsFolder, id, version);
  if (existsSync(folder)) {
    throw new Error(
      `apps/${id}/${version} is published already; a published version never changes`,
    );
  }
  if (
    request.repository === undefined ||
    request.commit === undefined ||
    request.source === undefined
  ) {
    throw new Error('--add needs --source, --repository and --commit');
  }
  const packageBytes = readFileSync(packagePath);
  const screenshotFolder = join(request.source, rules.appsFolder, id, rules.screenshots.folder);
  const screenshots = filesBelow(screenshotFolder)
    .filter((name) => !name.includes('/'))
    .map((name) => ({ name, bytes: readFileSync(join(screenshotFolder, name)) }));
  mkdirSync(screenshots.length > 0 ? join(folder, rules.screenshots.folder) : folder, {
    recursive: true,
  });
  writeFileSync(join(folder, packageName(manifest)), packageBytes);
  writeFileSync(join(folder, 'pithyx.json'), manifestBytes);
  writeFileSync(join(folder, 'icon.svg'), icon);
  for (const shot of screenshots) {
    writeFileSync(join(folder, rules.screenshots.folder, shot.name), shot.bytes);
  }
  const lockBytes = files.get(LOCK_FILE);
  const lock = lockBytes === undefined ? null : parseLock(lockBytes);
  if (lockBytes !== undefined && lock === null) {
    throw new Error(`${packagePath} has an invalid ${LOCK_FILE}`);
  }
  const architectures =
    lock === null
      ? [...ARCHITECTURES]
      : ARCHITECTURES.filter((name) =>
          Object.values(lock.images).every((platforms) => platforms[name] !== undefined),
        );
  if (architectures.length === 0) {
    throw new Error(`${packagePath}: no architecture has every image`);
  }
  const entry: VersionEntry = {
    publishedAt: timestamp(now),
    package: fileEntry(packageBytes),
    manifest: fileEntry(manifestBytes),
    icon: fileEntry(icon),
    ...(screenshots.length === 0
      ? {}
      : {
          screenshots: screenshots.map((shot) => ({ name: shot.name, ...fileEntry(shot.bytes) })),
        }),
    ...(lock === null ? {} : { images: lock.images }),
    architectures,
    pithyx: manifest.pithyx ?? '*',
    ...(manifest.dataVersion === undefined ? {} : { dataVersion: manifest.dataVersion }),
    license: manifest.license ?? '',
    source: {
      repository: request.repository,
      commit: request.commit,
      path: `${rules.appsFolder}/${id}`,
    },
  };
  return [id, version, entry];
}

function copyFolder(from: string, to: string) {
  for (const path of filesBelow(from)) {
    mkdirSync(join(to, path, '..'), { recursive: true });
    copyFileSync(join(from, path), join(to, path));
  }
}

/** The index as CI writes it: apps by id, versions by SemVer, two spaces, a final line break. */
export function formatIndex(index: IndexFile): string {
  const apps = Object.fromEntries(
    Object.keys(index.apps)
      .sort()
      .map((id) => {
        const versions = index.apps[id]?.versions ?? {};
        return [
          id,
          {
            versions: Object.fromEntries(
              Object.keys(versions)
                .sort(compareVersions)
                .map((version) => [version, versions[version]]),
            ),
          },
        ];
      }),
  );
  return `${JSON.stringify(
    {
      indexVersion: 1,
      catalog: index.catalog,
      channel: index.channel,
      sequence: index.sequence,
      publishedAt: index.publishedAt,
      apps,
    },
    null,
    2,
  )}\n`;
}

/**
 * `pithyx catalog index`: adds built packages (or promotes the versions of another branch) to a branch folder and
 * writes its `index.json` with the next sequence. Published versions are never changed: every file of a version
 * already in the index must still match it, and adding a version that exists fails. The old signature goes; sign the
 * new index afterwards. Returns what was added.
 */
export function writeIndex(request: IndexRequest): string[] {
  const now = request.now ?? new Date();
  const previous = readIndex(request.branch);
  const index: IndexFile = {
    catalog: request.catalog.id,
    channel: request.channel,
    sequence: (previous?.index.sequence ?? 0) + 1,
    publishedAt: timestamp(now),
    apps: structuredClone(previous?.index.apps ?? {}),
  };
  if (previous !== undefined) {
    const checked = validateIndex(previous.bytes, {
      catalog: request.catalog.id,
      channel: request.channel,
    });
    if (checked.index === undefined) {
      throw new IssuesError(`${INDEX_FILE} of the branch is not valid`, checked.issues);
    }
  }
  for (const [id, app] of Object.entries(index.apps)) {
    for (const [version, entry] of Object.entries(app.versions)) {
      verifyVersionFiles(
        join(request.branch, rules.appsFolder, id, version),
        id,
        version,
        entry as VersionEntry,
      );
    }
  }
  const added: string[] = [];
  const put = (id: string, version: string, entry: VersionEntry) => {
    (index.apps[id] ??= { versions: {} }).versions[version] = entry;
    added.push(`${id} ${version}`);
  };
  for (const packagePath of request.add ?? []) {
    put(...addPackage(request, packagePath, now));
  }
  if (request.promote !== undefined) {
    const from = readIndex(request.promote);
    if (from === undefined) {
      throw new Error(`${request.promote} has no ${INDEX_FILE} to promote`);
    }
    for (const [id, app] of Object.entries(from.index.apps)) {
      for (const [version, entry] of Object.entries(app.versions)) {
        if (index.apps[id]?.versions[version] !== undefined) {
          continue;
        }
        const source = join(request.promote, rules.appsFolder, id, version);
        verifyVersionFiles(source, id, version, entry as VersionEntry);
        copyFolder(source, join(request.branch, rules.appsFolder, id, version));
        put(id, version, entry as VersionEntry);
      }
    }
  }
  const text = formatIndex(index);
  const checked = validateIndex(Buffer.from(text), {
    catalog: request.catalog.id,
    channel: request.channel,
    knownSequence: previous?.index.sequence,
  });
  if (checked.index === undefined) {
    throw new IssuesError('the new index is not valid', checked.issues);
  }
  writeFileSync(join(request.branch, INDEX_FILE), text);
  rmSync(join(request.branch, SIGNATURE_FILE), { force: true });
  return added;
}

/** An Ed25519 private key from PEM (PKCS #8, as `openssl genpkey -algorithm ed25519` writes it). */
export function signingKey(pem: string): KeyObject {
  const key = createPrivateKey(pem);
  if (key.asymmetricKeyType !== 'ed25519') {
    throw new Error('the signing key is not an Ed25519 key');
  }
  return key;
}

/** The raw 32-byte public key of a private key in base64, as the box's keyring holds it. */
export function publicKeyOf(key: KeyObject): string {
  const jwk = createPublicKey(key).export({ format: 'jwk' });
  return Buffer.from(String(jwk.x), 'base64url').toString('base64');
}

/**
 * `pithyx catalog sign`: signs the exact bytes of the branch's `index.json` and writes `index.json.sig`. With `append`
 * the signatures of other keys stay, for a rotation; a signature by the same key id is replaced.
 */
export function signIndex(branch: string, keyId: string, key: KeyObject, append = false): void {
  const bytes = readFileSync(join(branch, INDEX_FILE));
  const signature = sign(null, bytes, key).toString('base64');
  const path = join(branch, SIGNATURE_FILE);
  const kept =
    append && existsSync(path)
      ? (
          JSON.parse(readFileSync(path, 'utf8')) as {
            signatures: { keyId: string; signature: string }[];
          }
        ).signatures.filter((entry) => entry.keyId !== keyId)
      : [];
  writeFileSync(
    path,
    `${JSON.stringify({ signatureVersion: 1, signatures: [...kept, { keyId, signature }] }, null, 2)}\n`,
  );
}

/**
 * `pithyx catalog verify`: checks a branch folder as a box does, the signature against the given keys (skipped
 * without keys, as for a custom catalog), the index, and every file of every version against its entry.
 */
export function verifyBranch(
  branch: string,
  expected: { catalog: string; channel: CatalogChannel; keys: readonly TrustedKey[] },
): Issue[] {
  const indexPath = join(branch, INDEX_FILE);
  if (!existsSync(indexPath)) {
    return [{ code: 'store.catalog.invalid_json', params: { file: INDEX_FILE } }];
  }
  const bytes = readFileSync(indexPath);
  const signaturePath = join(branch, SIGNATURE_FILE);
  const result =
    expected.keys.length === 0
      ? validateIndex(bytes, expected)
      : checkSignedIndex(
          bytes,
          existsSync(signaturePath) ? readFileSync(signaturePath) : undefined,
          expected.keys,
          expected,
        );
  if (result.index === undefined) {
    return result.issues;
  }
  const issues: Issue[] = [];
  for (const [id, app] of Object.entries(result.index.apps)) {
    for (const [version, entry] of Object.entries(app.versions)) {
      try {
        verifyVersionFiles(
          join(branch, rules.appsFolder, id, version),
          id,
          version,
          entry as VersionEntry,
        );
      } catch (failure) {
        if (failure instanceof IssuesError) {
          issues.push(...failure.issues);
        } else {
          throw failure;
        }
      }
    }
  }
  return issues;
}

/** A path relative to the working folder, for messages. */
export function shown(path: string): string {
  return relative(process.cwd(), path) || '.';
}
