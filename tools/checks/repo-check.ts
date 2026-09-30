// Repository-wide checks that linters do not cover. Runs in lefthook and CI.
import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import { basename, extname, join, relative } from 'node:path';

interface Problem {
  file: string;
  line: number;
  message: string;
}

// Written as a code point: Prettier turns a \u escape into the literal character, which the check would flag.
const EM_DASH = String.fromCodePoint(0x2014);

const BINARY_EXTENSIONS = new Set([
  '.png',
  '.jpg',
  '.jpeg',
  '.gif',
  '.webp',
  '.ico',
  '.avif',
  '.woff',
  '.woff2',
  '.ttf',
  '.otf',
  '.zip',
  '.gz',
  '.iso',
  '.img',
  '.pdf',
]);

type CommentSyntax = 'c' | 'hash' | 'markup';

const COMMENT_SYNTAX: Record<string, CommentSyntax> = {
  '.ts': 'c',
  '.tsx': 'c',
  '.js': 'c',
  '.jsx': 'c',
  '.mjs': 'c',
  '.cjs': 'c',
  '.cs': 'c',
  '.css': 'c',
  '.jsonc': 'c',
  '.yml': 'hash',
  '.yaml': 'hash',
  '.sh': 'hash',
  '.toml': 'hash',
  '.html': 'markup',
  '.svg': 'markup',
  '.xml': 'markup',
  '.csproj': 'markup',
  '.props': 'markup',
  '.targets': 'markup',
  '.slnx': 'markup',
};

const HASH_COMMENT_FILES = new Set([
  '.editorconfig',
  '.gitignore',
  '.gitattributes',
  '.prettierignore',
  '.nvmrc',
  'Dockerfile',
]);

function listFiles(): string[] {
  const output = execFileSync(
    'git',
    ['ls-files', '-z', '--cached', '--others', '--exclude-standard', '--deduplicate'],
    { encoding: 'utf8' },
  );
  return output.split('\0').filter((file) => file !== '');
}

function readText(file: string): string | undefined {
  if (BINARY_EXTENSIONS.has(extname(file).toLowerCase())) {
    return undefined;
  }
  let buffer: Buffer;
  try {
    buffer = readFileSync(file);
  } catch {
    // Deleted in the working tree but still in the index.
    return undefined;
  }
  return buffer.includes(0) ? undefined : buffer.toString('utf8');
}

function commentSyntax(file: string): CommentSyntax | undefined {
  if (HASH_COMMENT_FILES.has(basename(file))) {
    return 'hash';
  }
  return COMMENT_SYNTAX[extname(file).toLowerCase()];
}

// Returns [line, text] for every comment. Skips string literals so URLs do not count as comments.
export function extractComments(text: string, syntax: CommentSyntax): [number, string][] {
  const comments: [number, string][] = [];
  let line = 1;
  let i = 0;
  const lineAt = (end: number, start: number) => {
    for (let j = start; j < end; j++) {
      if (text[j] === '\n') {
        line++;
      }
    }
  };
  const readUntil = (terminator: string, from: number) => {
    const end = text.indexOf(terminator, from);
    return end === -1 ? text.length : end;
  };

  while (i < text.length) {
    const char = text[i];
    const next = text[i + 1];
    if (syntax === 'markup' && text.startsWith('<!--', i)) {
      const end = readUntil('-->', i + 4);
      comments.push([line, text.slice(i + 4, end)]);
      lineAt(end, i);
      i = end + 3;
      continue;
    }
    if (syntax === 'c' && char === '/' && next === '/') {
      const end = readUntil('\n', i);
      comments.push([line, text.slice(i + 2, end)]);
      i = end;
      continue;
    }
    if (syntax === 'c' && char === '/' && next === '*') {
      const end = readUntil('*/', i + 2);
      comments.push([line, text.slice(i + 2, end)]);
      lineAt(end, i);
      i = end + 2;
      continue;
    }
    if (syntax === 'hash' && char === '#' && (i === 0 || /\s/.test(text[i - 1] ?? ''))) {
      const end = readUntil('\n', i);
      comments.push([line, text.slice(i + 1, end)]);
      i = end;
      continue;
    }
    if (syntax === 'c' && (char === '"' || char === "'" || char === '`')) {
      let j = i + 1;
      while (j < text.length && text[j] !== char && !(char !== '`' && text[j] === '\n')) {
        j += text[j] === '\\' ? 2 : 1;
      }
      lineAt(j, i);
      i = j + 1;
      continue;
    }
    if (char === '\n') {
      line++;
    }
    i++;
  }
  return comments;
}

function checkEmDashes(file: string, text: string): Problem[] {
  return text
    .split('\n')
    .flatMap((content, index) =>
      content.includes(EM_DASH) ? [{ file, line: index + 1, message: 'em dash (U+2014)' }] : [],
    );
}

function checkCommentLanguage(file: string, text: string): Problem[] {
  const syntax = commentSyntax(file);
  if (syntax === undefined || file.startsWith('i18n/')) {
    return [];
  }
  return extractComments(text, syntax).flatMap(([line, comment]) =>
    // eslint-disable-next-line no-control-regex
    /[^\x00-\x7F]/.test(comment)
      ? [{ file, line, message: 'non-ASCII character in a comment (comments are English)' }]
      : [],
  );
}

// The project's first working title may only remain where it is outside the repository's control: the GitHub
// organization and repository, the checkout path on the dev VM and the serials of the dev VM's virtual test disks.
const OLD_NAME = /vyx(?:is|test)/i;
const OLD_NAME_ALLOWED = new RegExp(
  [
    String.raw`github\.com[/:]vyxis/vyxis`,
    String.raw`vyxis/vyxis#\d+`,
    String.raw`:-vyxis/vyxis\}`,
    '`vyxis/vyxis`',
    String.raw`code/vyxis\b`,
    'VYXTEST',
  ].join('|'),
  'g',
);

function checkOldName(file: string, text: string): Problem[] {
  // This file spells out the exceptions; the handoff describes the rename itself.
  if (file === 'tools/checks/repo-check.ts' || file === 'docs/dev/HANDOFF.md') {
    return [];
  }
  return text
    .split('\n')
    .flatMap((content, index) =>
      OLD_NAME.test(content.replace(OLD_NAME_ALLOWED, ''))
        ? [{ file, line: index + 1, message: 'old project name, the project is called Pithyx' }]
        : [],
    );
}

// The daemon build writes openapi.json; the TypeScript generated from it must match what is committed.
async function checkGenerated(): Promise<Problem[]> {
  const { generate, generatedDirectory } =
    await import('../../packages/api-client/scripts/generate.ts');
  const problems: Problem[] = [];
  for (const [name, content] of await generate()) {
    const path = join(generatedDirectory, name);
    const current = existsSync(path) ? readFileSync(path, 'utf8') : undefined;
    if (current !== content) {
      problems.push({
        file: relative(process.cwd(), path),
        line: 1,
        message: 'generated file is out of date, run pnpm generate',
      });
    }
  }
  return problems;
}

const problems: Problem[] = [];
for (const file of listFiles()) {
  if (OLD_NAME.test(file.replace(OLD_NAME_ALLOWED, ''))) {
    problems.push({
      file,
      line: 1,
      message: 'old project name in the path, the project is called Pithyx',
    });
  }
  const text = readText(file);
  if (text === undefined) {
    continue;
  }
  problems.push(
    ...checkEmDashes(file, text),
    ...checkCommentLanguage(file, text),
    ...checkOldName(file, text),
  );
}
problems.push(...(await checkGenerated()));

for (const { file, line, message } of problems) {
  console.error(`${file}:${String(line)}: ${message}`);
}
if (problems.length > 0) {
  process.exit(1);
}
