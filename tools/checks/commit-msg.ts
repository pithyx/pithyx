// Validates commit subjects: `<gitmoji> <type>(<scope>)!: <subject>` as defined in CLAUDE.md.
// Usage: commit-msg.ts <message-file>   (git commit-msg hook)
//        commit-msg.ts --range <rev>    (all non-merge commits in a revision range, for CI)
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';

const TYPES = [
  'feat',
  'fix',
  'docs',
  'style',
  'refactor',
  'perf',
  'test',
  'build',
  'ci',
  'chore',
  'revert',
];
const EMOJI = String.raw`\p{Extended_Pictographic}\u{FE0F}?(?:\u{200D}\p{Extended_Pictographic}\u{FE0F}?)*`;
const SUBJECT_PATTERN = new RegExp(
  String.raw`^${EMOJI} (?:${TYPES.join('|')})(?:\([a-z0-9-]+(?:/[a-z0-9-]+)*\))?!?: \S.*$`,
  'u',
);
const MAX_SUBJECT_LENGTH = 100;
const EM_DASH = String.fromCodePoint(0x2014);

export function validateSubject(subject: string): string | undefined {
  if (subject.startsWith('Merge ') || subject.startsWith('Revert "')) {
    return undefined;
  }
  if (!SUBJECT_PATTERN.test(subject)) {
    return `expected "<gitmoji> <type>(<scope>): <subject>" with type one of ${TYPES.join(', ')}`;
  }
  if (subject.includes(EM_DASH)) {
    return 'em dashes are not allowed';
  }
  if (subject.endsWith('.')) {
    return 'subject must not end with a period';
  }
  if (subject.length > MAX_SUBJECT_LENGTH) {
    return `subject is longer than ${String(MAX_SUBJECT_LENGTH)} characters`;
  }
  return undefined;
}

function firstLine(message: string): string {
  return (
    message
      .split('\n')
      .map((line) => line.trimEnd())
      .find((line) => line !== '' && !line.startsWith('#')) ?? ''
  );
}

function subjectsInRange(range: string): [string, string][] {
  const output = execFileSync('git', ['log', '--no-merges', '--format=%H%x00%s', range], {
    encoding: 'utf8',
  });
  return output
    .split('\n')
    .filter((line) => line !== '')
    .map((line) => {
      const [sha = '', subject = ''] = line.split('\0');
      return [sha.slice(0, 10), subject];
    });
}

const args = process.argv.slice(2);
const entries: [string, string][] =
  args[0] === '--range'
    ? subjectsInRange(args[1] ?? 'HEAD')
    : [['message', firstLine(readFileSync(args[0] ?? '.git/COMMIT_EDITMSG', 'utf8'))]];

let failed = false;
for (const [label, subject] of entries) {
  const problem = validateSubject(subject);
  if (problem !== undefined) {
    failed = true;
    console.error(`${label}: "${subject}": ${problem}`);
  }
}
if (failed) {
  console.error('Example: ✨ feat(desktop): add window snapping');
  process.exit(1);
}
