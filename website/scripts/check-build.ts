// Checks the built site (dist/) for what language models rely on: the llms files, a Markdown twin for every docs
// page, and twins that are plain Markdown without leftover MDX components.
import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';

const dist = join(import.meta.dirname, '..', 'dist');
const problems: string[] = [];

function walk(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    return statSync(path).isDirectory() ? walk(path) : [path];
  });
}

const files = walk(dist).map((path) => relative(dist, path));

for (const required of [
  'llms.txt',
  'llms-full.txt',
  'llms/faq.txt',
  'de/llms.txt',
  'de/llms-full.txt',
  'robots.txt',
  'CNAME',
  'sitemap-index.xml',
]) {
  if (!files.includes(required)) {
    problems.push(`missing ${required}`);
  }
}

let pages = 0;
let placeholders = 0;
for (const file of files) {
  const match = /^((?:de\/)?docs(?:\/.*)?)\/index\.html$/.exec(file);
  if (!match) {
    continue;
  }
  pages++;
  const twin = `${match[1] ?? ''}.md`;
  if (!existsSync(join(dist, twin))) {
    problems.push(`${file} has no Markdown twin ${twin}`);
    continue;
  }
  const markdown = readFileSync(join(dist, twin), 'utf8');
  if (/^\s*<\/?[A-Z][A-Za-z]*[\s/>]/m.test(markdown) || /^import\s/m.test(markdown)) {
    problems.push(`${twin} still contains an MDX component or import`);
  }
  if (/^Status: placeholder$/m.test(markdown)) {
    placeholders++;
  }
}

for (const index of ['llms.txt', 'de/llms.txt']) {
  const text = existsSync(join(dist, index)) ? readFileSync(join(dist, index), 'utf8') : '';
  for (const [, url] of text.matchAll(/\]\((https?:\/\/[^)]+)\)/g)) {
    const path = new URL(url ?? '').pathname.replace(/^\//, '');
    if (!files.includes(path)) {
      problems.push(`${index} links to ${path}, which is not built`);
    }
  }
}

if (problems.length > 0) {
  console.error(problems.join('\n'));
  process.exit(1);
}
console.log(
  `website: ${String(pages)} docs pages with Markdown twins, ${String(placeholders)} still placeholders`,
);
