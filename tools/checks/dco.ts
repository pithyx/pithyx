// Fails if a non-merge commit in the range lacks a Signed-off-by trailer matching its author.
// Usage: dco.ts <rev>   (for example origin/dev..HEAD, or a single sha to check all its ancestors)
import { execFileSync } from 'node:child_process';

const range = process.argv[2] ?? 'HEAD';
const output = execFileSync(
  'git',
  ['log', '--no-merges', '--format=%H%x00%an <%ae>%x00%B%x1e', range],
  { encoding: 'utf8' },
);

let failed = false;
for (const record of output.split('\x1e')) {
  const [sha = '', author = '', body = ''] = record.replace(/^\n/, '').split('\0');
  if (sha === '') {
    continue;
  }
  const signOffs = body
    .split('\n')
    .filter((line) => line.startsWith('Signed-off-by: '))
    .map((line) => line.slice('Signed-off-by: '.length).trim());
  if (!signOffs.includes(author)) {
    failed = true;
    console.error(`${sha.slice(0, 10)}: missing "Signed-off-by: ${author}" (use git commit -s)`);
  }
}
if (failed) {
  process.exit(1);
}
