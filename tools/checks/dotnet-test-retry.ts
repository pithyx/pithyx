// Runs `dotnet test` and, when it fails, reruns once only the failed tests that are known flakes. Any other failure
// stays a failure, and so does a known flake that fails again. The rerun is logged and annotated, so it stays visible.
// Usage: dotnet-test-retry.ts -- <arguments for dotnet test>
import { spawnSync } from 'node:child_process';
import { mkdtempSync, readFileSync, readdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

// Tests known to fail now and then on a loaded runner (CLAUDE.md, Known pitfalls).
const flakyTests = [
  'Pithyx.Daemon.Tests.Commands.ProcessCommandRunnerTests.StreamsLinesAndKillsTheCommandWhenCancelled',
];
// Messages of failures caused by the machine, not the code: SQLite contention under parallel tests.
const flakyMessages = [/database is locked/i];

interface Failure {
  className: string;
  method: string;
  message: string;
}

const separator = process.argv.indexOf('--');
const testArgs = process.argv.slice(separator + 1);
if (separator < 0 || testArgs.length === 0) {
  console.error('usage: dotnet-test-retry.ts -- <arguments for dotnet test>');
  process.exit(2);
}

const unescape = (text: string): string =>
  text
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&quot;/g, '"')
    .replace(/&apos;/g, "'")
    .replace(/&amp;/g, '&');

function failuresIn(directory: string): Failure[] {
  const failures: Failure[] = [];
  for (const file of readdirSync(directory).filter((name) => name.endsWith('.trx'))) {
    const trx = readFileSync(join(directory, file), 'utf8');
    const methods = new Map<string, { className: string; method: string }>();
    for (const [, id, body] of trx.matchAll(
      /<UnitTest [^>]*?\bid="([^"]+)"[^>]*>([\s\S]*?)<\/UnitTest>/g,
    )) {
      const className = /<TestMethod [^>]*?\bclassName="([^"]+)"/.exec(body ?? '')?.[1];
      const method = /<TestMethod [^>]*?\bname="([^"]+)"/.exec(body ?? '')?.[1];
      if (id !== undefined && className !== undefined && method !== undefined) {
        methods.set(id, { className: unescape(className), method: unescape(method) });
      }
    }
    for (const [, attributes, body] of trx.matchAll(
      /<UnitTestResult ([^>]*)>([\s\S]*?)<\/UnitTestResult>/g,
    )) {
      if (!/\boutcome="Failed"/.test(attributes ?? '')) {
        continue;
      }
      const method = methods.get(/\btestId="([^"]+)"/.exec(attributes ?? '')?.[1] ?? '');
      const message = /<Message>([\s\S]*?)<\/Message>/.exec(body ?? '')?.[1] ?? '';
      failures.push({
        className: method?.className ?? '',
        method: method?.method ?? '',
        message: unescape(message),
      });
    }
  }
  return failures;
}

const isFlaky = (failure: Failure): boolean =>
  flakyTests.includes(`${failure.className}.${failure.method}`) ||
  flakyMessages.some((pattern) => pattern.test(failure.message));

// The original filters are dropped: the rerun selects the failed methods only.
function withoutFilters(args: string[]): string[] {
  const kept: string[] = [];
  let skipping = false;
  for (const arg of args) {
    if (arg.startsWith('--')) {
      skipping = arg === '--filter' || arg.startsWith('--filter-');
    }
    if (!skipping) {
      kept.push(arg);
    }
  }
  return kept;
}

function run(args: string[], results: string, name: string): number {
  const reportArgs = [
    '--report-xunit-trx',
    '--report-xunit-trx-filename',
    `${name}.trx`,
    '--results-directory',
    results,
  ];
  return spawnSync('dotnet', ['test', ...args, ...reportArgs], { stdio: 'inherit' }).status ?? 1;
}

function main(first: string, second: string): number {
  const status = run(testArgs, first, 'first');
  if (status === 0) {
    return 0;
  }
  const failures = failuresIn(first);
  const unknown = failures.filter((failure) => !isFlaky(failure));
  if (failures.length === 0 || unknown.length > 0) {
    console.error(
      failures.length === 0
        ? 'The test run failed without a failed test, so nothing is retried.'
        : `${String(unknown.length)} failed test(s) are not known flakes, so nothing is retried.`,
    );
    return status;
  }

  const names = [...new Set(failures.map((failure) => `${failure.className}.${failure.method}`))];
  console.log(`::warning title=Flaky test retried::${names.join(', ')}`);
  console.log(`Retrying once, only the failed known flakes:\n  ${names.join('\n  ')}`);
  const retryArgs = [
    ...withoutFilters(testArgs),
    '--no-build',
    ...names.flatMap((name) => ['--filter-method', name]),
  ];
  const retryStatus = run(retryArgs, second, 'retry');
  console.log(
    retryStatus === 0
      ? `The retried test(s) passed; the first run failed on known flake(s): ${names.join(', ')}`
      : 'The retried test(s) failed again.',
  );
  return retryStatus;
}

const firstDirectory = mkdtempSync(join(tmpdir(), 'test-first-'));
const secondDirectory = mkdtempSync(join(tmpdir(), 'test-retry-'));
let code: number;
try {
  code = main(firstDirectory, secondDirectory);
} finally {
  rmSync(firstDirectory, { recursive: true, force: true });
  rmSync(secondDirectory, { recursive: true, force: true });
}
process.exit(code);
