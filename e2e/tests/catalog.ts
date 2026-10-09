import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdirSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { probeId, probePackage } from './probe';

/** The id of the local test catalog's catalog.json. */
export const testCatalogId = 'com.example.e2e';

function file(bytes: Buffer) {
  return { sha256: createHash('sha256').update(bytes).digest('hex'), size: bytes.length };
}

/**
 * A custom catalog in a local git repository, as a catalog's CI writes its branch catalog: the probe's package with its
 * manifest and icon and an unsigned index. The simulated daemon fetches file:// URLs; a box never does. Returns its URL.
 */
export function createTestCatalog(): string {
  const folder = mkdtempSync(join(tmpdir(), 'pithyx-e2e-catalog-'));
  const git = (...args: string[]) =>
    execFileSync('git', ['-C', folder, ...args], {
      env: {
        ...process.env,
        GIT_AUTHOR_NAME: 'E2E',
        GIT_AUTHOR_EMAIL: 'e2e@example.com',
        GIT_COMMITTER_NAME: 'E2E',
        GIT_COMMITTER_EMAIL: 'e2e@example.com',
      },
    });
  git('init', '--quiet', '--initial-branch=catalog');
  const probe = join(import.meta.dirname, '..', 'apps', 'sdk-probe');
  const manifestBytes = readFileSync(join(probe, 'pithyx.json'));
  const manifest = JSON.parse(manifestBytes.toString('utf8')) as { version: string };
  const icon = readFileSync(join(probe, 'icon.svg'));
  const packageBytes = readFileSync(probePackage());
  const versionFolder = join(folder, 'apps', probeId, manifest.version);
  mkdirSync(versionFolder, { recursive: true });
  writeFileSync(join(versionFolder, `${probeId}-${manifest.version}.pithyxapp`), packageBytes);
  writeFileSync(join(versionFolder, 'pithyx.json'), manifestBytes);
  writeFileSync(join(versionFolder, 'icon.svg'), icon);
  writeFileSync(
    join(folder, 'catalog.json'),
    JSON.stringify({
      catalogVersion: 1,
      id: testCatalogId,
      name: { en: 'E2E catalog' },
      images: 'ghcr.io/example/apps',
    }),
  );
  const index = {
    indexVersion: 1,
    catalog: testCatalogId,
    channel: 'stable',
    sequence: 1,
    publishedAt: '2026-10-08T20:00:00Z',
    apps: {
      [probeId]: {
        versions: {
          [manifest.version]: {
            publishedAt: '2026-10-08T20:00:00Z',
            package: file(packageBytes),
            manifest: file(manifestBytes),
            icon: file(icon),
            architectures: ['amd64', 'arm64'],
            pithyx: '*',
            license: 'MIT',
            source: {
              repository: 'https://example.com/e2e-catalog',
              commit: 'a'.repeat(40),
              path: `apps/${probeId}`,
            },
          },
        },
      },
    },
  };
  writeFileSync(join(folder, 'index.json'), JSON.stringify(index, null, 2));
  git('add', '--all');
  git('commit', '--quiet', '-m', 'publish');
  return `file://${folder}`;
}
