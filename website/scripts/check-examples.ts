// Checks that the docs show the examples under examples/ as they are: a code block after
// `<!-- example: <path> -->` must equal that file, in every language, and every example must appear on an English page.
// `tsc -p examples` checks the files themselves against the workspace packages.
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';

const website = join(import.meta.dirname, '..');
const examples = join(website, 'examples');
const docs = join(website, 'src', 'content', 'docs');
const problems: string[] = [];

function walk(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    return statSync(path).isDirectory() ? walk(path) : [path];
  });
}

const files = walk(examples)
  .map((path) => relative(examples, path))
  .filter((path) => path !== 'tsconfig.json');
const shown = new Set<string>();

for (const page of walk(docs).filter((path) => /\.mdx?$/.test(path))) {
  const text = readFileSync(page, 'utf8');
  const english = !relative(docs, page).startsWith('de/');
  for (const match of text.matchAll(
    /<!-- example: (\S+) -->\n\n?(`{3,})[^\n]*\n([\s\S]*?)\n\2\n/g,
  )) {
    const [, path = '', , code = ''] = match;
    const name = relative(website, page);
    if (!files.includes(path)) {
      problems.push(`${name} shows ${path}, which is not in examples/`);
      continue;
    }
    if (code !== readFileSync(join(examples, path), 'utf8').trimEnd()) {
      problems.push(`${name} shows ${path} differently from examples/${path}`);
    }
    if (english) {
      shown.add(path);
    }
  }
}

for (const file of files.filter((path) => !shown.has(path))) {
  problems.push(`examples/${file} is shown on no English docs page`);
}

if (problems.length > 0) {
  console.error(problems.join('\n'));
  process.exit(1);
}
console.log(`${String(files.length)} examples match their docs pages.`);
