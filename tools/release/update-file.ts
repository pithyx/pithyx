// The update files and channels of Pithyx (D338), as the release scripts write and check them and with the same
// verdicts as the box (daemon/src/Pithyx.Daemon/Updates); tools/release/fixtures is the corpus both run.
//
//   node tools/release/update-file.ts manifest <stage> --version <v> --channel <c> --minimum <v> --commit <sha>
//        --kernel <release> --zfs <version> [--changelog <file>]     writes <stage>/manifest.json for the files in it
//   node tools/release/update-file.ts sign <file> --key-id <id> [--append]
//        signs the exact bytes of <file> with the Ed25519 PEM key in PITHYX_SIGNING_KEY into <file>.sig
//   node tools/release/update-file.ts verify-manifest <manifest.json> --channel <box channel> [--allow-unsigned]
//   node tools/release/update-file.ts verify-unpacked <folder> --channel <box channel> [--allow-unsigned]
//        [--extra-key <id>:<public key>:<channels>]                  an unpacked update file: manifest and every file
//   node tools/release/update-file.ts verify-channel <channel.json> --channel <c> [--known-sequence <n>]
//        check a document and its .sig against the keys of tools/release/keys.json, as a box does
//   node tools/release/update-file.ts channel <manifest.json> --name <file> --url <url> --size <n> --sha256 <hex>
//        --sequence <n>                                               prints channel.json for an update file
//   node tools/release/update-file.ts public-key                     prints the public half of PITHYX_SIGNING_KEY
//
// A failed check prints its issues as JSON and exits 1.
import {
  createHash,
  createPrivateKey,
  createPublicKey,
  sign,
  verify,
  type KeyObject,
} from 'node:crypto';
import { existsSync, readdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { dirname, join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import { Ajv2020, type ValidateFunction } from 'ajv/dist/2020.js';

const here = dirname(fileURLToPath(import.meta.url));

export type Channel = 'stable' | 'beta';

export interface TrustedKey {
  keyId: string;
  publicKey: string;
  channels: string[];
}

export interface Issue {
  code: string;
  params: Record<string, string | number>;
}

export interface UpdateFileEntry {
  path: string;
  sha256: string;
  size: number;
}

export interface UpdateManifest {
  updateVersion: 1;
  version: string;
  channel: Channel;
  minimumVersion: string;
  architecture: string;
  commit: string;
  publishedAt: string;
  kernel: { release: string; zfs: string };
  files: UpdateFileEntry[];
  packages: { name: string; version: string; path: string }[];
  changelog: string;
}

export interface UpdateChannel {
  channelVersion: 1;
  channel: Channel;
  sequence: number;
  publishedAt: string;
  version: string;
  minimumVersion: string;
  changelog: string;
  file: { name: string; url: string; size: number; sha256: string };
}

const ajv = new Ajv2020({ allErrors: true, strict: true });
ajv.addFormat('date-time', true);

function schema(path: string): ValidateFunction {
  return ajv.compile(JSON.parse(readFileSync(path, 'utf8')) as object);
}

const validateSignature = schema(join(here, '../../sdk/schema/index-signature.v1.schema.json'));
const validateManifest = schema(join(here, 'schema/update-manifest.v1.schema.json'));
const validateChannel = schema(join(here, 'schema/update-channel.v1.schema.json'));

/** The keys a box trusts, with the channels each signs for; the daemon builds in the same (UpdateKeys). */
export function builtInKeys(): TrustedKey[] {
  return (JSON.parse(readFileSync(join(here, 'keys.json'), 'utf8')) as { keys: TrustedKey[] }).keys;
}

/**
 * Parses the bytes of a JSON file as the box does: UTF-8 without a byte order mark, and no key twice in an object
 * (JSON.parse would keep the last one).
 */
function parseStrict(bytes: Uint8Array): { value: unknown } | undefined {
  try {
    const text = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(bytes);
    const value: unknown = JSON.parse(text);
    return hasDuplicateKeys(text) ? undefined : { value };
  } catch {
    return undefined;
  }
}

/** The same scan as the CLI's (packages/cli/src/json-schema.ts): the box refuses a key twice in one object. */
function hasDuplicateKeys(text: string): boolean {
  const stack: { keys: Set<string> | undefined; expectKey: boolean }[] = [];
  for (let i = 0; i < text.length; i++) {
    const char = text[i];
    if (char === '"') {
      let end = i + 1;
      while (text[end] !== '"') {
        end += text[end] === '\\' ? 2 : 1;
      }
      const top = stack.at(-1);
      if (top?.keys !== undefined && top.expectKey) {
        const key = JSON.parse(text.slice(i, end + 1)) as string;
        if (top.keys.has(key)) {
          return true;
        }
        top.keys.add(key);
        top.expectKey = false;
      }
      i = end;
    } else if (char === '{') {
      stack.push({ keys: new Set(), expectKey: true });
    } else if (char === '[') {
      stack.push({ keys: undefined, expectKey: false });
    } else if (char === '}' || char === ']') {
      stack.pop();
    } else if (char === ',') {
      const top = stack.at(-1);
      if (top?.keys !== undefined) {
        top.expectKey = true;
      }
    }
  }
  return false;
}

function ed25519Key(publicKey: string): KeyObject {
  return createPublicKey({
    key: { kty: 'OKP', crv: 'Ed25519', x: Buffer.from(publicKey, 'base64').toString('base64url') },
    format: 'jwk',
  });
}

/**
 * Checks a signature file over the exact bytes of a document, before anything parses the document: at least one
 * signature by a key trusted for `channel`, and every signature by such a key valid; other keys are ignored, so a
 * release can be signed with an old and a new key while boxes move to the new one.
 */
export function verifySignature(
  document: Uint8Array,
  signature: Uint8Array | undefined,
  keyring: readonly TrustedKey[],
  channel: Channel,
): 'missing' | 'malformed' | 'untrusted' | 'mismatch' | undefined {
  if (signature === undefined) {
    return 'missing';
  }
  const parsed = parseStrict(signature);
  if (parsed === undefined || !validateSignature(parsed.value)) {
    return 'malformed';
  }
  const trusted = new Map(
    keyring.filter((key) => key.channels.includes(channel)).map((key) => [key.keyId, key]),
  );
  const relevant = (
    parsed.value as { signatures: { keyId: string; signature: string }[] }
  ).signatures.flatMap((entry) => {
    const key = trusted.get(entry.keyId);
    return key === undefined ? [] : [{ key, bytes: Buffer.from(entry.signature, 'base64') }];
  });
  if (relevant.length === 0) {
    return 'untrusted';
  }
  return relevant.every(({ key, bytes }) =>
    verify(null, document, ed25519Key(key.publicKey), bytes),
  )
    ? undefined
    : 'mismatch';
}

/** The channels whose files a box on `boxChannel` installs: a stable box only stable ones. */
export function acceptedChannels(boxChannel: Channel): Channel[] {
  return boxChannel === 'stable' ? ['stable'] : ['stable', 'beta'];
}

/**
 * Reads the manifest of an update file as the box does: the signature (keys trusted for the box's channel), then the
 * manifest itself and its channel. `allowUnsigned` is true only on a development box (D338).
 */
export function checkManifest(
  bytes: Uint8Array,
  signature: Uint8Array | undefined,
  keyring: readonly TrustedKey[],
  options: { boxChannel: Channel; allowUnsigned?: boolean | undefined },
): { issues: Issue[]; manifest?: UpdateManifest } {
  if (signature !== undefined || options.allowUnsigned !== true) {
    const reason = verifySignature(bytes, signature, keyring, options.boxChannel);
    if (reason !== undefined) {
      return { issues: [{ code: 'update.file.signature_invalid', params: { reason } }] };
    }
  }
  const parsed = parseStrict(bytes);
  if (parsed === undefined || !validateManifest(parsed.value)) {
    return { issues: [{ code: 'update.file.invalid', params: {} }] };
  }
  const manifest = parsed.value as UpdateManifest;
  if (!acceptedChannels(options.boxChannel).includes(manifest.channel)) {
    return {
      issues: [
        {
          code: 'update.file.channel',
          params: { channel: manifest.channel, boxChannel: options.boxChannel },
        },
      ],
    };
  }
  return { issues: [], manifest };
}

/** Reads a channel's `channel.json` as the box does: the signature, the document, its channel and its sequence. */
export function checkChannel(
  bytes: Uint8Array,
  signature: Uint8Array | undefined,
  keyring: readonly TrustedKey[],
  options: { boxChannel: Channel; knownSequence?: number | undefined },
): { issues: Issue[]; channel?: UpdateChannel } {
  const reason = verifySignature(bytes, signature, keyring, options.boxChannel);
  if (reason !== undefined) {
    return { issues: [{ code: 'update.channel.signature_invalid', params: { reason } }] };
  }
  const parsed = parseStrict(bytes);
  if (parsed === undefined || !validateChannel(parsed.value)) {
    return { issues: [{ code: 'update.channel.invalid', params: {} }] };
  }
  const channel = parsed.value as UpdateChannel;
  if (channel.channel !== options.boxChannel) {
    return {
      issues: [
        {
          code: 'update.channel.mismatch',
          params: { channel: channel.channel, expected: options.boxChannel },
        },
      ],
    };
  }
  if (options.knownSequence !== undefined && channel.sequence < options.knownSequence) {
    return {
      issues: [
        {
          code: 'update.channel.sequence_old',
          params: { sequence: channel.sequence, known: options.knownSequence },
        },
      ],
    };
  }
  return { issues: [], channel };
}

/**
 * Checks an unpacked update file as the box does before it installs anything: the manifest and its signature, then
 * every file the manifest lists with its size and SHA-256, and no file it does not list.
 */
export function checkUnpacked(
  folder: string,
  keyring: readonly TrustedKey[],
  options: { boxChannel: Channel; allowUnsigned?: boolean | undefined },
): { issues: Issue[]; manifest?: UpdateManifest } {
  const result = checkManifest(
    readFileSync(join(folder, 'manifest.json')),
    readOptional(join(folder, 'manifest.json.sig')),
    keyring,
    options,
  );
  if (result.manifest === undefined) {
    return result;
  }
  const listed = new Map(result.manifest.files.map((file) => [file.path, file]));
  const present = readdirSync(join(folder, 'debs')).map((name) => `debs/${name}`);
  const unexpected = present.find((path) => !listed.has(path));
  if (unexpected !== undefined) {
    return { issues: [{ code: 'update.file.unexpected_entry', params: { path: unexpected } }] };
  }
  for (const entry of result.manifest.files) {
    const path = join(folder, entry.path);
    if (!existsSync(path)) {
      return { issues: [{ code: 'update.file.missing_entry', params: { path: entry.path } }] };
    }
    if (statSync(path).size !== entry.size || sha256(path) !== entry.sha256) {
      return { issues: [{ code: 'update.file.hash_mismatch', params: { path: entry.path } }] };
    }
  }
  return result;
}

export function signingKey(pem: string): KeyObject {
  const key = createPrivateKey(pem);
  if (key.asymmetricKeyType !== 'ed25519') {
    throw new Error('the signing key is not an Ed25519 key');
  }
  return key;
}

export function publicKeyOf(key: KeyObject): string {
  const jwk = createPublicKey(key).export({ format: 'jwk' });
  return Buffer.from(jwk.x ?? '', 'base64url').toString('base64');
}

/** The signature file for `bytes`; with `existing`, the signatures of other keys stay (a key rotation). */
export function signatureFile(
  bytes: Uint8Array,
  keyId: string,
  key: KeyObject,
  existing?: Uint8Array,
): string {
  const kept =
    existing === undefined
      ? []
      : (
          JSON.parse(Buffer.from(existing).toString('utf8')) as {
            signatures: { keyId: string; signature: string }[];
          }
        ).signatures.filter((entry) => entry.keyId !== keyId);
  const signature = sign(null, bytes, key).toString('base64');
  return `${JSON.stringify({ signatureVersion: 1, signatures: [...kept, { keyId, signature }] }, null, 2)}\n`;
}

function sha256(path: string): string {
  return createHash('sha256').update(readFileSync(path)).digest('hex');
}

/** The Debian package name and version of a file named `<name>_<version>_<arch>.deb`. */
export function debName(file: string): { name: string; version: string } {
  const [name, version] = file.replace(/\.deb$/, '').split('_');
  if (name === undefined || version === undefined) {
    throw new Error(`${file} is not named <name>_<version>_<architecture>.deb`);
  }
  // A file name carries the epoch as %3a, the way apt saves it.
  return { name, version: decodeURIComponent(version) };
}

/** `manifest.json` for the files below `stage` (everything in `debs/`), in path order. */
export function buildManifest(
  stage: string,
  fields: Omit<UpdateManifest, 'updateVersion' | 'files' | 'packages' | 'architecture'> & {
    architecture?: string;
  },
): UpdateManifest {
  const debs = join(stage, 'debs');
  const files = readdirSync(debs)
    .sort()
    .map((name) => {
      const path = join(debs, name);
      return { path: relative(stage, path), sha256: sha256(path), size: statSync(path).size };
    });
  const packages = files
    .filter((file) => file.path.endsWith('.deb'))
    .map((file) => ({ ...debName(file.path.slice('debs/'.length)), path: file.path }));
  return {
    updateVersion: 1,
    version: fields.version,
    channel: fields.channel,
    minimumVersion: fields.minimumVersion,
    architecture: fields.architecture ?? 'amd64',
    commit: fields.commit,
    publishedAt: fields.publishedAt,
    kernel: fields.kernel,
    files,
    packages,
    changelog: fields.changelog,
  };
}

/** The section of CHANGELOG.md for `version` (or `Unreleased` when it has none yet), without its heading. */
export function changelogSection(text: string, version: string): string {
  const sections = text.split(/^## /m).slice(1);
  const pick = (name: string) =>
    sections.find((section) => section.startsWith(`[${name}]`) || section.startsWith(name));
  const section = pick(version) ?? pick('Unreleased') ?? '';
  return section.slice(section.indexOf('\n') + 1).trim();
}

function json(value: unknown): string {
  return `${JSON.stringify(value, null, 2)}\n`;
}

function options(args: string[]): Map<string, string | true> {
  const parsed = new Map<string, string | true>();
  for (let index = 0; index < args.length; index += 1) {
    const arg = args[index] ?? '';
    if (!arg.startsWith('--')) {
      continue;
    }
    const next = args[index + 1];
    if (next === undefined || next.startsWith('--')) {
      parsed.set(arg.slice(2), true);
    } else {
      parsed.set(arg.slice(2), next);
      index += 1;
    }
  }
  return parsed;
}

function required(values: Map<string, string | true>, name: string): string {
  const value = values.get(name);
  if (typeof value !== 'string') {
    throw new Error(`--${name} is missing`);
  }
  return value;
}

function channelOption(values: Map<string, string | true>): Channel {
  const value = required(values, 'channel');
  if (value !== 'stable' && value !== 'beta') {
    throw new Error(`--channel must be stable or beta, not ${value}`);
  }
  return value;
}

function readOptional(path: string): Uint8Array | undefined {
  return existsSync(path) ? readFileSync(path) : undefined;
}

function report(issues: Issue[]): number {
  if (issues.length > 0) {
    process.stderr.write(json(issues));
    return 1;
  }
  return 0;
}

function keyFromEnvironment(): KeyObject {
  const pem = process.env.PITHYX_SIGNING_KEY;
  if (pem === undefined || pem.trim() === '') {
    throw new Error('PITHYX_SIGNING_KEY holds no key');
  }
  return signingKey(pem);
}

function main(args: string[]): number {
  const [command, target] = args;
  const values = options(args.slice(2));
  const now = new Date().toISOString().replace(/\.\d{3}Z$/, 'Z');
  switch (command) {
    case 'manifest': {
      const stage = target ?? '';
      const changelog = values.get('changelog');
      const version = required(values, 'version');
      const manifest = buildManifest(stage, {
        version,
        channel: channelOption(values),
        minimumVersion: required(values, 'minimum'),
        commit: required(values, 'commit'),
        publishedAt:
          typeof values.get('published-at') === 'string' ? required(values, 'published-at') : now,
        kernel: { release: required(values, 'kernel'), zfs: required(values, 'zfs') },
        changelog:
          typeof changelog === 'string'
            ? changelogSection(readFileSync(changelog, 'utf8'), version)
            : '',
      });
      if (!validateManifest(manifest)) {
        throw new Error(`the manifest is invalid: ${ajv.errorsText(validateManifest.errors)}`);
      }
      writeFileSync(join(stage, 'manifest.json'), json(manifest));
      return 0;
    }
    case 'sign': {
      const file = target ?? '';
      const keyId = required(values, 'key-id');
      const existing = values.get('append') === true ? readOptional(`${file}.sig`) : undefined;
      writeFileSync(
        `${file}.sig`,
        signatureFile(readFileSync(file), keyId, keyFromEnvironment(), existing),
      );
      return 0;
    }
    case 'verify-manifest': {
      const file = target ?? '';
      const result = checkManifest(readFileSync(file), readOptional(`${file}.sig`), builtInKeys(), {
        boxChannel: channelOption(values),
        allowUnsigned: values.get('allow-unsigned') === true,
      });
      return report(result.issues);
    }
    case 'verify-unpacked': {
      const extra = values.get('extra-key');
      const keyring = builtInKeys();
      if (typeof extra === 'string') {
        const [keyId = '', publicKey = '', channels = ''] = extra.split(':');
        keyring.push({ keyId, publicKey, channels: channels.split(',') });
      }
      const result = checkUnpacked(target ?? '', keyring, {
        boxChannel: channelOption(values),
        allowUnsigned: values.get('allow-unsigned') === true,
      });
      return report(result.issues);
    }
    case 'verify-channel': {
      const file = target ?? '';
      const known = values.get('known-sequence');
      const result = checkChannel(readFileSync(file), readOptional(`${file}.sig`), builtInKeys(), {
        boxChannel: channelOption(values),
        knownSequence: typeof known === 'string' ? Number(known) : undefined,
      });
      return report(result.issues);
    }
    case 'channel': {
      const manifest = JSON.parse(readFileSync(target ?? '', 'utf8')) as UpdateManifest;
      const channel: UpdateChannel = {
        channelVersion: 1,
        channel: manifest.channel,
        sequence: Number(required(values, 'sequence')),
        publishedAt: now,
        version: manifest.version,
        minimumVersion: manifest.minimumVersion,
        changelog: manifest.changelog,
        file: {
          name: required(values, 'name'),
          url: required(values, 'url'),
          size: Number(required(values, 'size')),
          sha256: required(values, 'sha256'),
        },
      };
      if (!validateChannel(channel)) {
        throw new Error(`the channel is invalid: ${ajv.errorsText(validateChannel.errors)}`);
      }
      process.stdout.write(json(channel));
      return 0;
    }
    case 'public-key':
      process.stdout.write(`${publicKeyOf(keyFromEnvironment())}\n`);
      return 0;
    default:
      throw new Error(
        `unknown command ${command ?? ''}; see the head of tools/release/update-file.ts`,
      );
  }
}

if (process.argv[1] !== undefined && fileURLToPath(import.meta.url) === process.argv[1]) {
  try {
    process.exitCode = main(process.argv.slice(2));
  } catch (error) {
    process.stderr.write(
      `update-file: ${error instanceof Error ? error.message : String(error)}\n`,
    );
    process.exitCode = 2;
  }
}
