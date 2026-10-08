// The fixture corpus in sdk/schema/fixtures, shared with the daemon's xUnit suite, so the CLI and the box judge alike.
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { dirname, join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import type { AppManifest } from '@pithyx/sdk';
import { describe, expect, it } from 'vitest';
import rules from '@pithyx/sdk/schema/compose.v1.rules.json' with { type: 'json' };
import {
  checkSignedIndex,
  validateCatalog,
  validateIndex,
  type CatalogChannel,
  type TrustedKey,
} from './catalog';
import { checkCatalogPullRequest } from './catalog-rules';
import { validateCompose } from './compose';
import { validateIcon } from './icon';
import type { Issue } from './issues';
import { checkLayout } from './layout';
import { isSupportedElevatedRight, validateManifest } from './manifest';

const fixtures = join(dirname(fileURLToPath(import.meta.url)), '../../../sdk/schema/fixtures');

const files = (...parts: string[]) => readdirSync(join(fixtures, ...parts)).sort();
const read = (...parts: string[]) => readFileSync(join(fixtures, ...parts));
const json = (...parts: string[]): unknown => JSON.parse(read(...parts).toString('utf8'));

interface ExpectedIssue {
  code: string;
  params?: Record<string, string | number>;
}

const text = (value: unknown) => String(value);

function matches(issue: Issue, expected: ExpectedIssue): boolean {
  return (
    issue.code === expected.code &&
    Object.entries(expected.params ?? {}).every(
      ([name, value]) => text(issue.params[name]) === text(value),
    )
  );
}

describe('manifest corpus', () => {
  const expected = json('manifest', 'expected.json') as Record<string, ExpectedIssue[]>;

  it.each(files('manifest', 'valid'))('accepts %s', (file) => {
    const result = validateManifest(read('manifest', 'valid', file));
    expect(result.issues).toEqual([]);
    expect(result.manifest).toBeDefined();
  });

  it.each(files('manifest', 'invalid'))('refuses %s with the expected codes', (file) => {
    const issues = expected[file];
    expect(issues, `manifest/expected.json lacks ${file}`).toBeDefined();
    const result = validateManifest(read('manifest', 'invalid', file));
    expect(result.manifest).toBeUndefined();
    expect(new Set(result.issues.map((issue) => issue.code))).toEqual(
      new Set(issues?.map((issue) => issue.code)),
    );
    for (const issue of issues ?? []) {
      expect(
        result.issues.some((actual) => matches(actual, issue)),
        `${JSON.stringify(issue)} not in ${JSON.stringify(result.issues)}`,
      ).toBe(true);
    }
  });

  it('has a fixture for every expectation', () => {
    expect(Object.keys(expected).sort()).toEqual(files('manifest', 'invalid'));
  });

  it('lets system apps use the reserved namespace', () => {
    const result = validateManifest(read('manifest', 'invalid', 'id-reserved.json'), {
      allowReservedId: true,
    });
    expect(result.issues).toEqual([]);
  });

  it.each([
    ['docker.socket', false],
    ['host.full', false],
    ['capability:BPF', false],
    ['capability:NET_ADMIN', true],
    ['device:/dev/dri/renderD128', true],
    ['host.network', true],
  ] as const)('grants the elevated right %s: %s', (right, supported) => {
    expect(isSupportedElevatedRight(right)).toBe(supported);
  });
});

describe('compose corpus', () => {
  const expected = json('compose', 'expected.json') as Record<string, ExpectedIssue[]>;
  const manifests = json('compose', 'manifests.json') as Record<string, string>;
  const manifest = (kind: string, file: string): AppManifest => {
    const name = manifests[`${kind}/${file}`] ?? 'base.json';
    const result = validateManifest(read('compose', 'manifests', name));
    if (result.manifest === undefined) {
      throw new Error(`compose/manifests/${name} is not a valid manifest`);
    }
    return result.manifest;
  };

  it.each(files('compose', 'valid'))('accepts %s', (file) => {
    expect(validateCompose(read('compose', 'valid', file), manifest('valid', file)).issues).toEqual(
      [],
    );
  });

  it.each(files('compose', 'invalid'))('refuses %s with the expected codes', (file) => {
    const issues = expected[file];
    expect(issues, `compose/expected.json lacks ${file}`).toBeDefined();
    const result = validateCompose(read('compose', 'invalid', file), manifest('invalid', file));
    for (const issue of issues ?? []) {
      expect(
        result.issues.some((actual) => matches(actual, issue)),
        `${JSON.stringify(issue)} not in ${JSON.stringify(result.issues)}`,
      ).toBe(true);
    }
    // Every issue is expected, so both validators report exactly the same list.
    for (const actual of result.issues) {
      expect(
        issues?.some((issue) => matches(actual, issue)),
        `${JSON.stringify(actual)} is not expected`,
      ).toBe(true);
    }
  });

  it('has a fixture for every expectation and every manifest choice', () => {
    expect(Object.keys(expected).sort()).toEqual(files('compose', 'invalid'));
    for (const key of Object.keys(manifests)) {
      const [kind = '', file = ''] = key.split('/');
      expect(files('compose', kind)).toContain(file);
    }
  });

  describe('source form (compose.dev.yaml)', () => {
    const devExpected = json('compose', 'dev', 'expected.json') as Record<string, ExpectedIssue[]>;
    const base = manifest('valid', 'minimal.yaml');

    it.each(files('compose', 'dev', 'valid'))('accepts %s', (file) => {
      expect(
        validateCompose(read('compose', 'dev', 'valid', file), base, { dev: true }).issues,
      ).toEqual([]);
    });

    it.each(files('compose', 'dev', 'invalid'))('refuses %s with the expected codes', (file) => {
      const issues = devExpected[file];
      expect(issues, `compose/dev/expected.json lacks ${file}`).toBeDefined();
      const result = validateCompose(read('compose', 'dev', 'invalid', file), base, { dev: true });
      expect(result.issues.length).toBe(issues?.length);
      for (const issue of issues ?? []) {
        expect(
          result.issues.some((actual) => matches(actual, issue)),
          `${JSON.stringify(issue)} not in ${JSON.stringify(result.issues)}`,
        ).toBe(true);
      }
    });

    it('has a fixture for every expectation', () => {
      expect(Object.keys(devExpected).sort()).toEqual(files('compose', 'dev', 'invalid'));
    });

    it('refuses build in the form the box reads', () => {
      const bytes = read('compose', 'dev', 'valid', 'build-short.yaml');
      expect(validateCompose(bytes, base).issues.map((issue) => issue.code)).toContain(
        'app.compose.key_unsupported',
      );
    });
  });

  it('never allows a key it lists as refused', () => {
    const allowed = { top: rules.top, service: rules.service, volume: {}, mount: rules.mount.keys };
    for (const [area, keys] of Object.entries(rules.refused)) {
      for (const key of Object.keys(keys)) {
        expect(Object.keys(allowed[area as keyof typeof allowed])).not.toContain(key);
      }
    }
  });
});

describe('icon corpus', () => {
  const expected = json('icon', 'expected.json') as Record<string, Record<string, string>[]>;

  it.each(files('icon', 'valid'))('accepts %s', (file) => {
    expect(validateIcon(read('icon', 'valid', file))).toEqual([]);
  });

  it.each(files('icon', 'invalid'))('refuses %s for the expected reasons', (file) => {
    const reasons = expected[file];
    expect(reasons, `icon/expected.json lacks ${file}`).toBeDefined();
    const issues = validateIcon(read('icon', 'invalid', file));
    expect(issues.every((issue) => issue.code === 'app.icon.invalid')).toBe(true);
    expect(new Set(issues.map((issue) => JSON.stringify(sorted(issue.params))))).toEqual(
      new Set(reasons?.map((params) => JSON.stringify(sorted(params)))),
    );
  });

  it('has a fixture for every expectation', () => {
    expect(Object.keys(expected).sort()).toEqual(files('icon', 'invalid'));
  });
});

function sorted(params: Record<string, unknown>): [string, string][] {
  return Object.entries(params)
    .map(([name, value]): [string, string] => [name, text(value)])
    .sort(([a], [b]) => (a < b ? -1 : 1));
}

describe('layout corpus', () => {
  const cases = json('layout', 'cases.json') as {
    name: string;
    files: string[];
    entry: string;
    compose?: string;
    expected: { path: string; reason: string }[];
  }[];

  it.each(cases.map((entry) => [entry.name, entry] as const))('%s', (_, entry) => {
    const issues = checkLayout(entry.files, { entry: entry.entry, compose: entry.compose });
    expect(issues.every((issue) => issue.code === 'app.package.layout_invalid')).toBe(true);
    expect(
      new Set(issues.map((issue) => JSON.stringify([issue.params.path, issue.params.reason]))),
    ).toEqual(new Set(entry.expected.map(({ path, reason }) => JSON.stringify([path, reason]))));
  });
});

/** Every issue expected and nothing else, in any order: both validators report exactly the same list. */
function expectExactly(actual: Issue[], expected: ExpectedIssue[]): void {
  expect(actual.length, JSON.stringify(actual)).toBe(expected.length);
  for (const issue of expected) {
    expect(
      actual.some((candidate) => matches(candidate, issue)),
      `${JSON.stringify(issue)} not in ${JSON.stringify(actual)}`,
    ).toBe(true);
  }
  for (const candidate of actual) {
    expect(
      expected.some((issue) => matches(candidate, issue)),
      `${JSON.stringify(candidate)} is not expected`,
    ).toBe(true);
  }
}

describe('catalog corpus', () => {
  describe('catalog.json', () => {
    const expected = json('catalog', 'catalogs', 'expected.json') as Record<
      string,
      ExpectedIssue[]
    >;
    const options = json('catalog', 'catalogs', 'options.json') as Record<
      string,
      { allowReservedId?: boolean }
    >;

    it.each(files('catalog', 'catalogs', 'valid'))('accepts %s', (file) => {
      const result = validateCatalog(
        read('catalog', 'catalogs', 'valid', file),
        options[`valid/${file}`],
      );
      expect(result.issues).toEqual([]);
      expect(result.catalog).toBeDefined();
    });

    it.each(files('catalog', 'catalogs', 'invalid'))('refuses %s', (file) => {
      const result = validateCatalog(
        read('catalog', 'catalogs', 'invalid', file),
        options[`invalid/${file}`],
      );
      expect(result.catalog).toBeUndefined();
      expectExactly(result.issues, expected[file] ?? []);
    });

    it('has a fixture for every expectation', () => {
      expect(Object.keys(expected).sort()).toEqual(files('catalog', 'catalogs', 'invalid'));
    });
  });

  describe('index.json', () => {
    const expected = json('catalog', 'index', 'expected.json') as Record<string, ExpectedIssue[]>;
    const options = json('catalog', 'index', 'options.json') as Record<
      string,
      { catalog?: string; channel?: CatalogChannel; knownSequence?: number }
    >;
    const expectation = (key: string) => ({
      catalog: 'org.pithyx.official',
      channel: 'stable' as const,
      ...options[key],
    });

    it.each(files('catalog', 'index', 'valid'))('accepts %s', (file) => {
      const result = validateIndex(
        read('catalog', 'index', 'valid', file),
        expectation(`valid/${file}`),
      );
      expect(result.issues).toEqual([]);
      expect(result.index).toBeDefined();
    });

    it.each(files('catalog', 'index', 'invalid'))('refuses %s', (file) => {
      const result = validateIndex(
        read('catalog', 'index', 'invalid', file),
        expectation(`invalid/${file}`),
      );
      expect(result.index).toBeUndefined();
      expectExactly(result.issues, expected[file] ?? []);
    });

    it('has a fixture for every expectation and option', () => {
      expect(Object.keys(expected).sort()).toEqual(files('catalog', 'index', 'invalid'));
      for (const key of Object.keys(options)) {
        const [kind = '', file = ''] = key.split('/');
        expect(files('catalog', 'index', kind)).toContain(file);
      }
    });
  });

  describe('signatures', () => {
    const keys = (json('catalog', 'keys.json') as { keys: (TrustedKey & { seed: string })[] }).keys;
    const cases = json('catalog', 'signatures', 'cases.json') as {
      name: string;
      index: string;
      signature: string | null;
      catalog: string;
      channel: CatalogChannel;
      knownSequence?: number;
      expected: ExpectedIssue[];
    }[];

    it.each(cases.map((entry) => [entry.name, entry] as const))('%s', (_, entry) => {
      const result = checkSignedIndex(
        read('catalog', 'signatures', entry.index),
        entry.signature === null ? undefined : read('catalog', 'signatures', entry.signature),
        keys,
        { catalog: entry.catalog, channel: entry.channel, knownSequence: entry.knownSequence },
      );
      expectExactly(result.issues, entry.expected);
      expect(result.index === undefined).toBe(entry.expected.length > 0);
    });
  });

  describe('pull request rules', () => {
    interface FileSource {
      text?: string;
      base64?: string;
      fixture?: string;
      lines?: { count: number; width: number };
      bytes?: { hex: string; size: number };
    }
    const { cases } = json('catalog', 'rules', 'cases.json') as {
      cases: {
        name: string;
        catalog?: string;
        author?: string;
        folder?: string;
        manifest?: Record<string, unknown>;
        files?: Record<string, FileSource | null>;
        changed?: string[];
        alsoChanged?: string[];
        published?: string[];
        maintainers?: string[];
        expected: ExpectedIssue[];
      }[];
    };
    const baseDirectory = join(fixtures, 'catalog', 'rules', 'base');
    const walk = (directory: string): string[] =>
      readdirSync(directory).flatMap((name) => {
        const path = join(directory, name);
        return statSync(path).isDirectory() ? walk(path) : [path];
      });
    const base = new Map(
      walk(baseDirectory).map((path) => [relative(baseDirectory, path), readFileSync(path)]),
    );
    const bytes = (source: FileSource): Uint8Array => {
      if (source.text !== undefined) {
        return Buffer.from(source.text, 'utf8');
      }
      if (source.base64 !== undefined) {
        return Buffer.from(source.base64, 'base64');
      }
      if (source.fixture !== undefined) {
        return read(source.fixture);
      }
      if (source.lines !== undefined) {
        return Buffer.from(`${'a'.repeat(source.lines.width)}\n`.repeat(source.lines.count));
      }
      const { hex, size } = source.bytes ?? { hex: '', size: 0 };
      const result = Buffer.alloc(size);
      Buffer.from(hex, 'hex').copy(result);
      return result;
    };

    it.each(cases.map((entry) => [entry.name, entry] as const))('%s', (_, entry) => {
      const tree = new Map(base);
      if (entry.manifest !== undefined) {
        const manifest = Object.fromEntries(
          Object.entries({
            ...(JSON.parse(
              (base.get('pithyx.json') ?? Buffer.alloc(0)).toString('utf8'),
            ) as object),
            ...entry.manifest,
          }).filter(([, value]) => value !== null),
        );
        tree.set('pithyx.json', Buffer.from(`${JSON.stringify(manifest, null, 2)}\n`));
      }
      for (const [path, source] of Object.entries(entry.files ?? {})) {
        if (source === null) {
          tree.delete(path);
        } else {
          tree.set(path, Buffer.from(bytes(source)));
        }
      }
      const root = `apps/${entry.folder ?? 'com.example.notes'}/`;
      const files = new Map([...tree].map(([path, content]) => [`${root}${path}`, content]));
      const issues = checkCatalogPullRequest({
        catalog: entry.catalog ?? 'org.pithyx.community',
        author: entry.author ?? 'jan-dev',
        changed: [...(entry.changed ?? files.keys()), ...(entry.alsoChanged ?? [])],
        files,
        published: entry.published ?? [],
        maintainers: entry.maintainers,
      });
      expectExactly(issues, entry.expected);
    });
  });
});
