// Collects the Debian source packages for every binary package in a Pithyx image, so a release can ship the
// corresponding source the GPL asks for next to the ISO and the update files. The package lists come from
// image/build.sh: <out>/pithyx-<version>-<arch>.packages (the root file system) and .host-packages (GRUB, which
// build.sh takes from the build host for BOOTX64.EFI), and .built-using names the sources a package was built from
// besides its own (the kernel `linux` behind the signed linux-signed-amd64, Go libraries). Every file comes from snapshot.debian.org, which keeps every
// version Debian ever published, and is checked against its SHA-1 there; a second run only fetches what is missing.
//
//   node tools/release/collect-sources.ts --version <v> [--image-out image/out] [--out <folder>] [--pack]
//
// Writes <out>/<source>_<version>/<files> and <out>/sources.tsv (source, version, the binaries it covers). --pack
// also writes pithyx-<version>-sources-<n>.tar parts of at most 1.9 GB each next to the folder, for release assets.
// Pithyx's own packages are not fetched: pithyx and pithyx-kernel are built from this repository at the release tag,
// and pithyx-zfs-modules-<kver> is zfs-dkms output, so its source is Debian's zfs-linux of the same version.
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import {
  existsSync,
  mkdirSync,
  readFileSync,
  readdirSync,
  renameSync,
  statSync,
  writeFileSync,
} from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const SNAPSHOT = 'https://snapshot.debian.org';
const ARCHIVES = new Set(['debian', 'debian-security']);
const PART_LIMIT = 1_900_000_000;

export interface Binary {
  name: string;
  version: string;
}

export interface Source {
  name: string;
  version: string;
  binaries: string[];
}

/** The lines `name<TAB>version<TAB>arch` of a package list. */
export function parsePackages(text: string): Binary[] {
  return text
    .split('\n')
    .map((line) => line.trim())
    .filter((line) => line !== '' && !line.startsWith('#'))
    .map((line) => {
      const [name = '', version = ''] = line.split('\t');
      if (name === '' || version === '') {
        throw new Error(`not a package line: ${line}`);
      }
      return { name, version };
    });
}

/**
 * The binary package to look up for a package of the list, or null for Pithyx's own packages, whose source is this
 * repository. The ZFS module package is zfs-dkms output and has zfs-dkms's version.
 */
export function lookupName(binary: Binary): Binary | null {
  if (binary.name === 'pithyx' || binary.name === 'pithyx-kernel') {
    return null;
  }
  if (binary.name.startsWith('pithyx-zfs-modules-')) {
    return { name: 'zfs-dkms', version: binary.version };
  }
  return binary;
}

interface BinaryVersion {
  binary_version: string;
  source: string;
  version: string;
}

/** The source package and version that built a binary version, from snapshot.debian.org's `/mr/binary/<name>/`. */
export function sourceOf(
  binary: Binary,
  versions: BinaryVersion[],
): { name: string; version: string } {
  const match = versions.find((entry) => entry.binary_version === binary.version);
  if (match === undefined) {
    throw new Error(`snapshot.debian.org knows no ${binary.name} ${binary.version}`);
  }
  return { name: match.source, version: match.version };
}

/** Sources with the binaries they cover, sorted by name; one entry per source and version. */
export function groupSources(
  pairs: { binary: string; source: { name: string; version: string } }[],
): Source[] {
  const sources = new Map<string, Source>();
  for (const { binary, source } of pairs) {
    const key = `${source.name}\t${source.version}`;
    const entry = sources.get(key) ?? { ...source, binaries: [] };
    if (!entry.binaries.includes(binary)) {
      entry.binaries.push(binary);
    }
    sources.set(key, entry);
  }
  return [...sources.values()]
    .map((source) => ({ ...source, binaries: source.binaries.sort() }))
    .sort((a, b) => a.name.localeCompare(b.name) || a.version.localeCompare(b.version));
}

interface FileInfo {
  archive_name: string;
  name: string;
}

/** A source package's files (name and SHA-1) from `/mr/package/<source>/<version>/srcfiles?fileinfo=1`. */
export function sourceFiles(
  fileinfo: Record<string, FileInfo[]>,
): { name: string; sha1: string }[] {
  const files = new Map<string, string>();
  for (const [sha1, infos] of Object.entries(fileinfo)) {
    const info = infos.find((entry) => ARCHIVES.has(entry.archive_name));
    if (info !== undefined) {
      files.set(info.name, sha1);
    }
  }
  if (files.size === 0) {
    throw new Error('no files in the debian archives');
  }
  return [...files]
    .map(([name, sha1]) => ({ name, sha1 }))
    .sort((a, b) => a.name.localeCompare(b.name));
}

/**
 * The sources of `Built-Using` fields, from lines `package<TAB>linux (= 6.12.111-1), golang-1.24 (= 1.24.4-1)`, each
 * with the package that names it.
 */
export function parseBuiltUsing(
  text: string,
): { binary: string; source: { name: string; version: string } }[] {
  return text
    .split('\n')
    .filter((line) => line.trim() !== '')
    .flatMap((line) => {
      const [binary = '', field = ''] = line.split('\t');
      return field
        .split(',')
        .map((entry) => entry.trim())
        .filter((entry) => entry !== '')
        .map((entry) => {
          const match = /^(\S+) \(= ([^)\s]+)\)$/.exec(entry);
          if (match?.[1] === undefined || match[2] === undefined) {
            throw new Error(`not a Built-Using entry of ${binary}: ${entry}`);
          }
          return { binary, source: { name: match[1], version: match[2] } };
        });
    });
}

/** Folders split into parts of at most `limit` bytes, in order; a folder larger than the limit gets a part alone. */
export function planParts(folders: { name: string; bytes: number }[], limit: number): string[][] {
  const parts: string[][] = [];
  let current: string[] = [];
  let size = 0;
  for (const folder of folders) {
    if (current.length > 0 && size + folder.bytes > limit) {
      parts.push(current);
      current = [];
      size = 0;
    }
    current.push(folder.name);
    size += folder.bytes;
  }
  if (current.length > 0) {
    parts.push(current);
  }
  return parts;
}

async function fetchWithRetry(url: string): Promise<Response> {
  for (let attempt = 1; ; attempt++) {
    try {
      const response = await fetch(url);
      if (response.ok) {
        return response;
      }
      if (response.status === 404 || attempt >= 5) {
        throw new Error(`${url}: HTTP ${String(response.status)}`);
      }
    } catch (error) {
      if (attempt >= 5 || (error instanceof Error && error.message.includes('HTTP 404'))) {
        throw error;
      }
    }
    await new Promise((done) => setTimeout(done, 2000 * 2 ** (attempt - 1)));
  }
}

async function json<T>(url: string): Promise<T> {
  return (await (await fetchWithRetry(url)).json()) as T;
}

function sha1Of(path: string): string {
  return createHash('sha1').update(readFileSync(path)).digest('hex');
}

async function download(sha1: string, path: string): Promise<void> {
  if (existsSync(path) && sha1Of(path) === sha1) {
    return;
  }
  const data = Buffer.from(await (await fetchWithRetry(`${SNAPSHOT}/file/${sha1}`)).arrayBuffer());
  const actual = createHash('sha1').update(data).digest('hex');
  if (actual !== sha1) {
    throw new Error(`${path}: SHA-1 ${actual}, expected ${sha1}`);
  }
  writeFileSync(`${path}.part`, data);
  renameSync(`${path}.part`, path);
}

/** Runs `work` over the items, at most `limit` at a time. */
async function eachLimited<T>(
  items: T[],
  limit: number,
  work: (item: T) => Promise<void>,
): Promise<void> {
  let next = 0;
  await Promise.all(
    Array.from({ length: Math.min(limit, items.length) }, async () => {
      while (next < items.length) {
        const item = items[next++] as T;
        await work(item);
      }
    }),
  );
}

function folderBytes(folder: string): number {
  return readdirSync(folder).reduce((sum, file) => sum + statSync(join(folder, file)).size, 0);
}

async function main(args: string[]): Promise<void> {
  const option = (name: string) => {
    const index = args.indexOf(name);
    return index >= 0 ? args[index + 1] : undefined;
  };
  const version = option('--version');
  if (version === undefined) {
    throw new Error(
      'usage: collect-sources.ts --version <v> [--image-out image/out] [--out <folder>] [--pack]',
    );
  }
  const repo = resolve(dirname(fileURLToPath(import.meta.url)), '../..');
  const imageOut = resolve(option('--image-out') ?? join(repo, 'image/out'));
  const out = resolve(option('--out') ?? join(imageOut, `pithyx-${version}-sources`));
  const [packages, hostPackages, builtUsing] = ['packages', 'host-packages', 'built-using'].map(
    (kind) => join(imageOut, `pithyx-${version}-amd64.${kind}`),
  ) as [string, string, string];
  const lists = [packages, hostPackages];
  for (const list of [...lists, builtUsing]) {
    if (!existsSync(list)) {
      throw new Error(
        `${list} is missing; build the image with image/build.sh --version ${version} first`,
      );
    }
  }

  const binaries = lists.flatMap((list) => parsePackages(readFileSync(list, 'utf8')));
  const pairs: { binary: string; source: { name: string; version: string } }[] = [];
  await eachLimited(binaries, 4, async (binary) => {
    const lookup = lookupName(binary);
    if (lookup === null) {
      return;
    }
    const answer = await json<{ result: BinaryVersion[] }>(
      `${SNAPSHOT}/mr/binary/${encodeURIComponent(lookup.name)}/`,
    );
    pairs.push({ binary: binary.name, source: sourceOf(lookup, answer.result) });
  });
  pairs.push(...parseBuiltUsing(readFileSync(builtUsing, 'utf8')));
  // The host packages carry their Built-Using as a fourth column.
  const hostBuiltUsing = readFileSync(hostPackages, 'utf8')
    .split('\n')
    .map((line) => line.split('\t'))
    .filter((columns) => (columns[3] ?? '').trim() !== '')
    .map((columns) => `${columns[0] ?? ''}\t${columns[3] ?? ''}`)
    .join('\n');
  pairs.push(...parseBuiltUsing(hostBuiltUsing));
  const sources = groupSources(pairs);
  console.log(
    `${String(binaries.length)} packages come from ${String(sources.length)} source packages`,
  );

  mkdirSync(out, { recursive: true });
  let done = 0;
  await eachLimited(sources, 4, async (source) => {
    const answer = await json<{ fileinfo: Record<string, FileInfo[]> }>(
      `${SNAPSHOT}/mr/package/${encodeURIComponent(source.name)}/${encodeURIComponent(source.version)}/srcfiles?fileinfo=1`,
    );
    const folder = join(out, `${source.name}_${source.version}`);
    mkdirSync(folder, { recursive: true });
    for (const file of sourceFiles(answer.fileinfo)) {
      await download(file.sha1, join(folder, file.name));
    }
    done++;
    if (done % 25 === 0 || done === sources.length) {
      console.log(`${String(done)} of ${String(sources.length)} source packages`);
    }
  });
  writeFileSync(
    join(out, 'sources.tsv'),
    `${sources.map((source) => [source.name, source.version, source.binaries.join(',')].join('\t')).join('\n')}\n`,
  );

  if (args.includes('--pack')) {
    const folders = sources.map((source) => {
      const name = `${source.name}_${source.version}`;
      return { name, bytes: folderBytes(join(out, name)) };
    });
    const parts = planParts(folders, PART_LIMIT);
    parts.forEach((part, index) => {
      const tar = join(dirname(out), `pithyx-${version}-sources-${String(index + 1)}.tar`);
      const members = index === 0 ? ['sources.tsv', ...part] : part;
      execFileSync(
        'tar',
        ['--sort=name', '--owner=0', '--group=0', '--numeric-owner', '-cf', tar, ...members],
        { cwd: out },
      );
      console.log(`wrote ${tar}`);
    });
  }
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  main(process.argv.slice(2)).catch((error: unknown) => {
    console.error(`collect-sources: ${error instanceof Error ? error.message : String(error)}`);
    process.exit(1);
  });
}
