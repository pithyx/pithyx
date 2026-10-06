// The fixture corpus in sdk/schema/fixtures, shared with the daemon's xUnit suite, so the CLI and the box judge alike.
import { readdirSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
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
