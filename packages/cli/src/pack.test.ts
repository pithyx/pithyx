import {
  cpSync,
  mkdirSync,
  mkdtempSync,
  readdirSync,
  readFileSync,
  rmSync,
  utimesSync,
  writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { gunzipSync } from 'node:zlib';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { formatLock, LOCK_FILE } from './lock';
import { pack, packageName } from './pack';
import { readProject, TEMPLATE_ICON_PATH } from './project';

const fixtures = join(dirname(fileURLToPath(import.meta.url)), '../../../sdk/schema/fixtures');
const fixtureProject = join(fixtures, 'package', 'project');
const fixturePackage = join(fixtures, 'package', 'com.example.package-fixture-1.0.0.pithyxapp');

let scratch: string;

beforeEach(() => {
  scratch = mkdtempSync(join(tmpdir(), 'pithyx-pack-test-'));
});

afterEach(() => {
  rmSync(scratch, { recursive: true, force: true });
});

function copyProject(name: string): string {
  const target = join(scratch, name);
  cpSync(fixtureProject, target, { recursive: true });
  return target;
}

function touchAll(folder: string, seconds: number): void {
  for (const entry of readdirSync(folder, { withFileTypes: true, recursive: true })) {
    utimesSync(join(entry.parentPath, entry.name), seconds, seconds);
  }
}

/** The names of the entries of a tar archive, pax names included. */
function tarNames(archive: Buffer): string[] {
  const names: string[] = [];
  let pending: string | undefined;
  for (let offset = 0; offset + 512 <= archive.length;) {
    const header = archive.subarray(offset, offset + 512);
    if (header.every((byte) => byte === 0)) {
      break;
    }
    const text = (start: number, length: number) =>
      header
        .subarray(start, start + length)
        .toString('ascii')
        .replace(/\0.*$/s, '');
    const size = parseInt(text(124, 12), 8);
    const data = archive.subarray(offset + 512, offset + 512 + size);
    if (text(156, 1) === 'x') {
      pending = /path=(.*)\n/.exec(data.toString('ascii'))?.[1];
    } else {
      const prefix = text(345, 155);
      names.push(pending ?? (prefix === '' ? text(0, 100) : `${prefix}/${text(0, 100)}`));
      pending = undefined;
    }
    offset += 512 + Math.ceil(size / 512) * 512;
  }
  return names;
}

describe('pithyx pack', () => {
  it('gives the same bytes for the same files, whatever their times on disk', async () => {
    const first = copyProject('first');
    const second = copyProject('second');
    touchAll(first, 1_000_000_000);
    touchAll(second, 1_800_000_000);
    await pack(readProject(first, { frontend: 'build' }), join(scratch, 'first.pithyxapp'));
    await pack(readProject(second, { frontend: 'build' }), join(scratch, 'second.pithyxapp'));

    const one = readFileSync(join(scratch, 'first.pithyxapp'));
    expect(readFileSync(join(scratch, 'second.pithyxapp')).equals(one)).toBe(true);
    // gzip without a time and with the "unknown" platform.
    expect([...one.subarray(4, 10)]).toEqual([0, 0, 0, 0, 2, 255]);
  });

  it('builds the archive of the shared fixture, which the daemon reads in CliPackageTests', async () => {
    const project = readProject(copyProject('fixture'), { frontend: 'build' });
    expect(project.findings).toEqual([]);
    const target = join(scratch, packageName(project.manifest ?? { id: '', version: '' }));
    await pack(project, target);

    // The tar inside, not the gzip bytes, so a newer zlib does not break the comparison.
    const archive = gunzipSync(readFileSync(target));
    expect(archive.equals(gunzipSync(readFileSync(fixturePackage)))).toBe(true);
    expect(tarNames(archive)).toEqual([
      'pithyx.json',
      'icon.svg',
      'CHECKSUMS',
      'LICENSE',
      'README.md',
      `frontend/assets/${'a'.repeat(120)}.txt`,
      'frontend/assets/app.js',
      'frontend/assets/folder-with-a-rather-long-name-number-one/folder-with-a-rather-long-name-number-two/prefixed.txt',
      'frontend/index.html',
    ]);
  });

  it('writes owners, modes and times as fixed values', async () => {
    const project = readProject(copyProject('fixed'), { frontend: 'build' });
    await pack(project, join(scratch, 'fixed.pithyxapp'));
    const header = gunzipSync(readFileSync(join(scratch, 'fixed.pithyxapp'))).subarray(0, 512);
    const field = (start: number, length: number) =>
      header.subarray(start, start + length).toString('ascii');
    expect(field(100, 8)).toBe('0000644\0');
    expect(field(108, 16)).toBe('0000000\x000000000\0');
    expect(field(136, 12)).toBe('00000000000\0');
    expect(field(257, 8)).toBe('ustar\x0000');
  });
});

describe('pithyx validate', () => {
  const codes = (root: string, frontend = 'build') =>
    readProject(root, { frontend }).findings.map((finding) => `${finding.level} ${finding.code}`);

  it('warns while the icon is the template icon', () => {
    const root = copyProject('template');
    cpSync(TEMPLATE_ICON_PATH, join(root, 'icon.svg'));
    expect(codes(root)).toEqual(['warning cli.icon.template']);
  });

  it('leaves hidden files of the build out with a warning', () => {
    const root = copyProject('hidden');
    mkdirSync(join(root, 'build', '.vite'));
    writeFileSync(join(root, 'build', '.vite', 'manifest.json'), '{}');
    const project = readProject(root, { frontend: 'build' });
    expect(project.findings).toEqual([
      { level: 'warning', code: 'cli.frontend.skipped', params: { path: 'build/.vite' } },
    ]);
    expect(project.entries.some((entry) => entry.path.includes('.vite'))).toBe(false);
  });

  it('needs the build and the entry in it', () => {
    const root = copyProject('unbuilt');
    expect(codes(root, 'dist')).toEqual([
      'error cli.frontend.missing',
      'error app.package.layout_invalid',
    ]);
    rmSync(join(root, 'build', 'index.html'));
    expect(codes(root)).toEqual(['error app.package.layout_invalid']);
  });

  it('checks the manifest and the icon as the box does', () => {
    const root = copyProject('broken');
    writeFileSync(join(root, 'pithyx.json'), '{"manifestVersion": 1}');
    writeFileSync(join(root, 'icon.svg'), '<svg xmlns="http://www.w3.org/2000/svg"/>');
    expect(new Set(codes(root))).toEqual(
      new Set(['error app.manifest.schema_violation', 'error app.icon.invalid']),
    );
  });

  describe('with a backend', () => {
    const image = `ghcr.io/example/chat-server:1.2.0@sha256:${'a3f5'.repeat(16)}`;
    const digest = (n: string) => `sha256:${n.repeat(64)}`;

    function backendProject(): string {
      const root = copyProject('backend');
      cpSync(join(fixtures, 'compose', 'manifests', 'base.json'), join(root, 'pithyx.json'));
      mkdirSync(join(root, 'backend'));
      cpSync(
        join(fixtures, 'compose', 'valid', 'minimal.yaml'),
        join(root, 'backend', 'compose.yaml'),
      );
      return root;
    }

    it('needs the images lock', () => {
      expect(codes(backendProject())).toEqual(['error cli.lock.missing']);
    });

    it('takes the Compose file and a current lock into the package', () => {
      const root = backendProject();
      writeFileSync(
        join(root, LOCK_FILE),
        formatLock({
          lockVersion: 1,
          images: { [image]: { amd64: { manifest: digest('1'), config: digest('2') } } },
        }),
      );
      const project = readProject(root, { frontend: 'build' });
      expect(project.findings).toEqual([
        {
          level: 'warning',
          code: 'cli.lock.platform_missing',
          params: { image, arch: 'arm64' },
        },
      ]);
      expect(project.entries.map((entry) => entry.path)).toEqual(
        expect.arrayContaining(['backend/compose.yaml', LOCK_FILE]),
      );
    });

    it('refuses a lock that does not match the Compose file', () => {
      const root = backendProject();
      const other = `ghcr.io/example/other:1@sha256:${'b'.repeat(64)}`;
      writeFileSync(
        join(root, LOCK_FILE),
        formatLock({
          lockVersion: 1,
          images: { [other]: { amd64: { manifest: digest('1'), config: digest('2') } } },
        }),
      );
      expect(readProject(root, { frontend: 'build' }).findings).toEqual([
        {
          level: 'error',
          code: 'cli.lock.stale',
          params: { path: LOCK_FILE, reason: 'missing', image },
        },
        {
          level: 'error',
          code: 'cli.lock.stale',
          params: { path: LOCK_FILE, reason: 'unused', image: other },
        },
        {
          level: 'warning',
          code: 'cli.lock.platform_missing',
          params: { image: other, arch: 'arm64' },
        },
      ]);
      writeFileSync(join(root, LOCK_FILE), '{"lockVersion": 2, "images": {}}');
      expect(codes(root)).toEqual(['error cli.lock.invalid']);
    });
  });
});

describe('a backend in its source form', () => {
  it('is a warning for vite build and an error for pack', () => {
    const root = copyProject('unbuilt');
    const manifest = JSON.parse(readFileSync(join(root, 'pithyx.json'), 'utf8')) as Record<
      string,
      unknown
    >;
    manifest.backend = {
      compose: 'backend/compose.yaml',
      endpoints: { api: { service: 'server', port: 8080 } },
      lifecycle: 'always',
    };
    writeFileSync(join(root, 'pithyx.json'), JSON.stringify(manifest));
    mkdirSync(join(root, 'backend'), { recursive: true });
    writeFileSync(
      join(root, 'backend', 'compose.dev.yaml'),
      'services:\n  server:\n    build: ./server\n',
    );
    const relaxed = readProject(root, { frontend: 'build', allowUnbuiltBackend: true });
    expect(relaxed.findings.filter((finding) => finding.level === 'error')).toEqual([]);
    expect(relaxed.findings.map((finding) => finding.code)).toContain('cli.backend.unbuilt');
    const strict = readProject(root, { frontend: 'build' });
    expect(strict.findings.some((finding) => finding.level === 'error')).toBe(true);
  });
});
