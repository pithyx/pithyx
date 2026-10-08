import { createPublicKey, verify } from 'node:crypto';
import catalogSchema from '@pithyx/sdk/schema/catalog.v1.schema.json' with { type: 'json' };
import rules from '@pithyx/sdk/schema/catalog.v1.rules.json' with { type: 'json' };
import signatureSchema from '@pithyx/sdk/schema/index-signature.v1.schema.json' with { type: 'json' };
import indexSchema from '@pithyx/sdk/schema/index.v1.schema.json' with { type: 'json' };
import type { Issue } from './issues';
import {
  compileSchema,
  display,
  escapePointer,
  isObject,
  parseStrictJson,
  schemaViolations,
} from './json-schema';
import { compareVersions } from './semver';

export const SUPPORTED_CATALOG_VERSION = 1;
export const SUPPORTED_INDEX_VERSION = 1;

/** The channel of a catalog branch: `stable` is `catalog`, `beta` is `catalog-next`. */
export type CatalogChannel = 'stable' | 'beta';

/** A key whose signatures a box accepts on the given channels. */
export interface TrustedKey {
  keyId: string;
  /** The 32 bytes of the Ed25519 public key in base64. */
  publicKey: string;
  channels: readonly string[];
}

/** What a box expects of an index before it reads it. */
export interface IndexExpectation {
  /** The id of the catalog the index was fetched for. */
  catalog: string;
  channel: CatalogChannel;
  /** The highest sequence the box verified for this catalog and channel, if any. */
  knownSequence?: number | undefined;
}

export interface CatalogFile {
  catalogVersion: number;
  id: string;
  name: Record<string, string>;
  images: string;
}

export interface IndexFile {
  catalog: string;
  channel: CatalogChannel;
  sequence: number;
  publishedAt: string;
  apps: Record<string, { versions: Record<string, IndexVersion> }>;
}

export interface IndexVersion {
  architectures: string[];
  images?: Record<string, Record<string, { manifest: string; config: string }>>;
  dataVersion?: number;
  source: { repository: string; commit: string; path: string };
}

const validateCatalogSchema = compileSchema(catalogSchema);
const validateIndexSchema = compileSchema(indexSchema);
const validateSignatureSchema = compileSchema(signatureSchema);

/** Whether a catalog may list apps in the reserved namespace `org.pithyx.*`. */
export function mayUseReservedIds(catalog: string): boolean {
  return rules.reservedIds.catalogs.includes(catalog);
}

function isReserved(id: string): boolean {
  return id.startsWith(rules.reservedIds.prefix);
}

/**
 * Reads a file of a catalog repository up to its schema, as the box does: refuses bytes that are not strict JSON, an
 * unknown format version and every schema violation. Returns the value only when there is no issue.
 */
function readFile(
  bytes: Uint8Array,
  file: 'catalog.json' | 'index.json',
  versionField: string,
  supported: number,
  validate: ReturnType<typeof compileSchema>,
): { issues: Issue[]; value?: Record<string, unknown> } {
  const parsed = parseStrictJson(bytes);
  if (parsed === undefined) {
    return { issues: [{ code: 'store.catalog.invalid_json', params: { file } }] };
  }
  const root = parsed.value;
  if (isObject(root) && versionField in root && root[versionField] !== supported) {
    return {
      issues: [
        {
          code: 'store.catalog.version_unsupported',
          params: { file, version: display(root[versionField]), supported },
        },
      ],
    };
  }
  const issues: Issue[] = schemaViolations(validate, root).map(([path, keyword]) => ({
    code: 'store.catalog.schema_violation',
    params: { file, path, keyword },
  }));
  return issues.length === 0 ? { issues, value: root as Record<string, unknown> } : { issues };
}

/**
 * Checks `catalog.json` of a catalog repository with the same verdicts and codes as the box (the corpus in
 * `@pithyx/sdk/schema/fixtures/catalog` holds both alike). An id in `org.pithyx.*` is refused unless
 * `allowReservedId` is set, which only the project's own catalogs may do.
 */
export function validateCatalog(
  bytes: Uint8Array,
  options: { allowReservedId?: boolean } = {},
): { issues: Issue[]; catalog?: CatalogFile } {
  const { issues, value } = readFile(
    bytes,
    'catalog.json',
    'catalogVersion',
    SUPPORTED_CATALOG_VERSION,
    validateCatalogSchema,
  );
  if (value === undefined) {
    return { issues };
  }
  const catalog = value as unknown as CatalogFile;
  if (options.allowReservedId !== true && isReserved(catalog.id)) {
    return {
      issues: [{ code: 'store.catalog.id_reserved', params: { path: '/id', id: catalog.id } }],
    };
  }
  return { issues, catalog };
}

/**
 * Checks `index.json` of a catalog branch against its schema and the rules a schema cannot express, with the same
 * verdicts and codes as the box: the catalog and channel it was fetched for, a sequence not below the known one, ids in
 * `org.pithyx.*` only in the official catalog, the source folder of each app, every architecture in every image and a
 * `dataVersion` that never goes down from one version to the next. Signed indexes go through `checkSignedIndex`.
 */
export function validateIndex(
  bytes: Uint8Array,
  expected: IndexExpectation,
): { issues: Issue[]; index?: IndexFile } {
  const { issues, value } = readFile(
    bytes,
    'index.json',
    'indexVersion',
    SUPPORTED_INDEX_VERSION,
    validateIndexSchema,
  );
  if (value === undefined) {
    return { issues };
  }
  const index = value as unknown as IndexFile;
  issues.push(...indexIssues(index, expected));
  return issues.length === 0 ? { issues, index } : { issues };
}

function mismatch(path: string, value: string, expected: string): Issue {
  return { code: 'store.catalog.mismatch', params: { path, value, expected } };
}

function indexIssues(index: IndexFile, expected: IndexExpectation): Issue[] {
  const issues: Issue[] = [];
  if (index.catalog !== expected.catalog) {
    issues.push(mismatch('/catalog', index.catalog, expected.catalog));
  }
  if (index.channel !== expected.channel) {
    issues.push(mismatch('/channel', index.channel, expected.channel));
  }
  if (expected.knownSequence !== undefined && index.sequence < expected.knownSequence) {
    issues.push({
      code: 'store.catalog.sequence_old',
      params: { sequence: index.sequence, known: expected.knownSequence },
    });
  }
  const reservedAllowed = mayUseReservedIds(expected.catalog);
  for (const [id, app] of Object.entries(index.apps)) {
    const appPath = `/apps/${escapePointer(id)}`;
    if (!reservedAllowed && isReserved(id)) {
      issues.push({ code: 'store.catalog.id_reserved', params: { path: appPath, id } });
    }
    let previous: number | undefined;
    const versions = Object.entries(app.versions).sort(([a], [b]) => compareVersions(a, b));
    for (const [version, entry] of versions) {
      const path = `${appPath}/versions/${escapePointer(version)}`;
      if (entry.source.path !== `${rules.appsFolder}/${id}`) {
        issues.push(
          mismatch(`${path}/source/path`, entry.source.path, `${rules.appsFolder}/${id}`),
        );
      }
      for (const [image, platforms] of Object.entries(entry.images ?? {})) {
        for (const architecture of entry.architectures) {
          if (!Object.hasOwn(platforms, architecture)) {
            issues.push({
              code: 'store.catalog.architecture_missing',
              params: { path: `${path}/images/${escapePointer(image)}`, architecture },
            });
          }
        }
      }
      if (entry.dataVersion !== undefined) {
        if (previous !== undefined && entry.dataVersion < previous) {
          issues.push({
            code: 'store.catalog.data_version_decreased',
            params: { path: `${path}/dataVersion`, dataVersion: entry.dataVersion, previous },
          });
        }
        previous = Math.max(previous ?? entry.dataVersion, entry.dataVersion);
      }
    }
  }
  return issues;
}

function signatureInvalid(reason: string): Issue[] {
  return [{ code: 'store.catalog.signature_invalid', params: { reason } }];
}

function ed25519Key(publicKey: string) {
  return createPublicKey({
    key: { kty: 'OKP', crv: 'Ed25519', x: Buffer.from(publicKey, 'base64').toString('base64url') },
    format: 'jwk',
  });
}

/**
 * Verifies `index.json.sig` over the exact bytes of `index.json`, before anything parses the index: at least one
 * signature by a key trusted for the channel, and every signature by such a key valid; signatures by other keys are
 * ignored. `signature` is undefined when the branch has no signature file.
 */
export function verifyIndexSignature(
  index: Uint8Array,
  signature: Uint8Array | undefined,
  keyring: readonly TrustedKey[],
  channel: CatalogChannel,
): Issue[] {
  if (signature === undefined) {
    return signatureInvalid('missing');
  }
  const parsed = parseStrictJson(signature);
  if (parsed === undefined || !validateSignatureSchema(parsed.value)) {
    return signatureInvalid('malformed');
  }
  const trusted = new Map(
    keyring.filter((key) => key.channels.includes(channel)).map((key) => [key.keyId, key]),
  );
  const signatures = (parsed.value as { signatures: { keyId: string; signature: string }[] })
    .signatures;
  const relevant = signatures.flatMap((entry) => {
    const key = trusted.get(entry.keyId);
    return key === undefined ? [] : [{ key, signature: Buffer.from(entry.signature, 'base64') }];
  });
  if (relevant.length === 0) {
    return signatureInvalid('untrusted');
  }
  const valid = relevant.every(({ key, signature: bytes }) =>
    verify(null, index, ed25519Key(key.publicKey), bytes),
  );
  return valid ? [] : signatureInvalid('mismatch');
}

/** Reads a signed index as the box does: the signature first, then the index itself. */
export function checkSignedIndex(
  index: Uint8Array,
  signature: Uint8Array | undefined,
  keyring: readonly TrustedKey[],
  expected: IndexExpectation,
): { issues: Issue[]; index?: IndexFile } {
  const issues = verifyIndexSignature(index, signature, keyring, expected.channel);
  return issues.length > 0 ? { issues } : validateIndex(index, expected);
}
