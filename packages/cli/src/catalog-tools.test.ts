import { execFileSync } from 'node:child_process';
import { createHash, createPrivateKey, generateKeyPairSync } from 'node:crypto';
import {
  cpSync,
  existsSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import {
  checkPullRequest,
  IssuesError,
  pendingVersions,
  publicKeyOf,
  readIndex,
  signIndex,
  signingKey,
  verifyBranch,
  writeIndex,
} from './catalog-tools';
import type { CatalogFile } from './catalog';

const fixtures = join(dirname(fileURLToPath(import.meta.url)), '../../../sdk/schema/fixtures');
const fixturePackage = join(fixtures, 'package', 'com.example.package-fixture-1.0.0.pithyxapp');
const ruleApp = join(fixtures, 'catalog', 'rules', 'base');

const catalog: CatalogFile = {
  catalogVersion: 1,
  id: 'com.example.apps',
  name: { en: 'Example apps' },
  images: 'ghcr.io/example/apps',
};

let scratch: string;

beforeEach(() => {
  scratch = mkdtempSync(join(tmpdir(), 'pithyx-catalog-test-'));
});

afterEach(() => {
  rmSync(scratch, { recursive: true, force: true });
});

function git(root: string, ...args: string[]): string {
  return execFileSync('git', ['-C', root, ...args], {
    encoding: 'utf8',
    env: {
      ...process.env,
      GIT_AUTHOR_NAME: 'Test',
      GIT_AUTHOR_EMAIL: 'test@example.com',
      GIT_COMMITTER_NAME: 'Test',
      GIT_COMMITTER_EMAIL: 'test@example.com',
    },
  });
}

/** A source checkout with a screenshot for the fixture package's app. */
function source(): string {
  const root = join(scratch, 'source');
  mkdirSync(join(root, 'apps', 'com.example.package-fixture', 'screenshots'), { recursive: true });
  // The PNG magic is all the index needs; the rules check screenshots in pull requests.
  writeFileSync(
    join(root, 'apps', 'com.example.package-fixture', 'screenshots', 'main.png'),
    Buffer.from('89504e470d0a1a0a0000', 'hex'),
  );
  return root;
}

function branch(name: string): string {
  const folder = join(scratch, name);
  mkdirSync(folder, { recursive: true });
  return folder;
}

const add = (folder: string, now = new Date('2026-10-08T20:00:00Z')) =>
  writeIndex({
    branch: folder,
    catalog,
    channel: 'beta',
    add: [fixturePackage],
    source: source(),
    repository: 'https://github.com/example/apps',
    commit: 'a'.repeat(40),
    now,
  });

describe('catalog index', () => {
  it('adds a package with its files, hashes and source, and starts the sequence at 1', () => {
    const next = branch('next');
    expect(add(next)).toEqual(['com.example.package-fixture 1.0.0']);
    const folder = join(next, 'apps', 'com.example.package-fixture', '1.0.0');
    expect(existsSync(join(folder, 'com.example.package-fixture-1.0.0.pithyxapp'))).toBe(true);
    const index = readIndex(next)?.index;
    expect(index?.sequence).toBe(1);
    expect(index?.channel).toBe('beta');
    const entry = index?.apps['com.example.package-fixture']?.versions[
      '1.0.0'
    ] as unknown as Record<string, unknown>;
    expect(entry.package).toEqual({
      sha256: createHash('sha256').update(readFileSync(fixturePackage)).digest('hex'),
      size: readFileSync(fixturePackage).length,
    });
    expect(entry.architectures).toEqual(['amd64', 'arm64']);
    expect(entry.pithyx).toBe('*');
    expect(entry.license).toBe('MIT');
    expect(entry.screenshots).toEqual([expect.objectContaining({ name: 'main.png', size: 10 })]);
    expect(entry.source).toEqual({
      repository: 'https://github.com/example/apps',
      commit: 'a'.repeat(40),
      path: 'apps/com.example.package-fixture',
    });
    expect(readFileSync(join(folder, 'pithyx.json'))).toEqual(
      readFileSync(join(fixtures, 'package', 'project', 'pithyx.json')),
    );
  });

  it('never overwrites a published version and raises the sequence on every write', () => {
    const next = branch('next');
    add(next);
    expect(() => add(next)).toThrow(/published already/);
    writeIndex({ branch: next, catalog, channel: 'beta' });
    expect(readIndex(next)?.index.sequence).toBe(2);
  });

  it('refuses to go on when a published file was changed', () => {
    const next = branch('next');
    add(next);
    writeFileSync(join(next, 'apps', 'com.example.package-fixture', '1.0.0', 'icon.svg'), '<svg/>');
    expect(() => writeIndex({ branch: next, catalog, channel: 'beta' })).toThrow(IssuesError);
  });

  it('promotes the versions of catalog-next with their entries into catalog', () => {
    const next = branch('next');
    add(next);
    const stable = branch('stable');
    expect(writeIndex({ branch: stable, catalog, channel: 'stable', promote: next })).toEqual([
      'com.example.package-fixture 1.0.0',
    ]);
    const promoted = readIndex(stable)?.index;
    expect(promoted?.channel).toBe('stable');
    expect(promoted?.apps).toEqual(readIndex(next)?.index.apps);
    expect(writeIndex({ branch: stable, catalog, channel: 'stable', promote: next })).toEqual([]);
  });
});

describe('catalog sign and verify', () => {
  const pem = () =>
    generateKeyPairSync('ed25519').privateKey.export({ format: 'pem', type: 'pkcs8' }).toString();

  it('signs the exact bytes and verifies the branch as a box does', () => {
    const next = branch('next');
    add(next);
    const key = signingKey(pem());
    signIndex(next, 'test-next', key);
    const trusted = [{ keyId: 'test-next', publicKey: publicKeyOf(key), channels: ['beta'] }];
    const expected = { catalog: catalog.id, channel: 'beta' as const, keys: trusted };
    expect(verifyBranch(next, expected)).toEqual([]);
    expect(verifyBranch(next, { ...expected, channel: 'stable' })).not.toEqual([]);
    const other = signingKey(pem());
    expect(
      verifyBranch(next, {
        ...expected,
        keys: [{ keyId: 'test-next', publicKey: publicKeyOf(other), channels: ['beta'] }],
      }),
    ).toEqual([{ code: 'store.catalog.signature_invalid', params: { reason: 'mismatch' } }]);
  });

  it('keeps the signatures of other keys with append, for a rotation', () => {
    const next = branch('next');
    add(next);
    signIndex(next, 'old', signingKey(pem()));
    signIndex(next, 'new', signingKey(pem()), true);
    const file = JSON.parse(readFileSync(join(next, 'index.json.sig'), 'utf8')) as {
      signatures: { keyId: string }[];
    };
    expect(file.signatures.map((entry) => entry.keyId)).toEqual(['old', 'new']);
  });

  it('finds a tampered package', () => {
    const next = branch('next');
    add(next);
    const target = join(
      next,
      'apps',
      'com.example.package-fixture',
      '1.0.0',
      'com.example.package-fixture-1.0.0.pithyxapp',
    );
    writeFileSync(target, Buffer.concat([readFileSync(target), Buffer.from([0])]));
    expect(verifyBranch(next, { catalog: catalog.id, channel: 'beta', keys: [] })).toEqual([
      {
        code: 'store.package.hash_mismatch',
        params: {
          path: 'apps/com.example.package-fixture/1.0.0/com.example.package-fixture-1.0.0.pithyxapp',
        },
      },
    ]);
  });

  it('reads a PEM key and refuses other kinds', () => {
    const rsa = generateKeyPairSync('rsa', { modulusLength: 1024 })
      .privateKey.export({ format: 'pem', type: 'pkcs8' })
      .toString();
    expect(() => signingKey(rsa)).toThrow(/not an Ed25519 key/);
    expect(signingKey(pem()).asymmetricKeyType).toBe('ed25519');
    expect(createPrivateKey(pem()).asymmetricKeyType).toBe('ed25519');
  });
});

describe('catalog check and pending', () => {
  function repository(): { root: string; base: string } {
    const root = join(scratch, 'repo');
    mkdirSync(root);
    git(root, 'init', '--quiet', '--initial-branch=master');
    writeFileSync(join(root, 'catalog.json'), `${JSON.stringify(catalog)}\n`);
    git(root, 'add', '.');
    git(root, 'commit', '--quiet', '-m', 'catalog');
    const base = git(root, 'rev-parse', 'HEAD').trim();
    cpSync(ruleApp, join(root, 'apps', 'com.example.notes'), { recursive: true });
    git(root, 'add', '.');
    git(root, 'commit', '--quiet', '-m', 'notes');
    return { root, base };
  }

  it('passes a new app from a maintainer it names and refuses anybody else', () => {
    const { root, base } = repository();
    const maintainers = JSON.parse(readFileSync(join(ruleApp, 'maintainers.json'), 'utf8')) as {
      maintainers: string[];
    };
    const author = maintainers.maintainers[0] ?? '';
    expect(checkPullRequest({ root, base, author })).toEqual([]);
    expect(checkPullRequest({ root, base, author: 'somebody-else' })).toEqual([
      {
        code: 'store.catalog.rule_violation',
        params: { rule: 'maintainer', author: 'somebody-else' },
      },
    ]);
  });

  it('refuses a version that catalog-next has published', () => {
    const { root, base } = repository();
    const author =
      (
        JSON.parse(readFileSync(join(ruleApp, 'maintainers.json'), 'utf8')) as {
          maintainers: string[];
        }
      ).maintainers[0] ?? '';
    const index = join(scratch, 'index.json');
    writeFileSync(
      index,
      JSON.stringify({ apps: { 'com.example.notes': { versions: { '1.1.0': {} } } } }),
    );
    expect(
      checkPullRequest({ root, base, author, index }).map((issue) => issue.params.rule),
    ).toEqual(['version_not_higher']);
    expect(pendingVersions(root, index)).toEqual([]);
    expect(pendingVersions(root)).toEqual([
      { id: 'com.example.notes', version: '1.1.0', path: 'apps/com.example.notes', backend: true },
    ]);
  });
});
