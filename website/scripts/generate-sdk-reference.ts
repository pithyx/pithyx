// Generates the API reference of @pithyx/sdk and @pithyx/ui under src/content/docs/docs/reference/sdk/ (and the same
// under de/), before `astro build`. TypeDoc with typedoc-plugin-markdown reads the packages' TSDoc through their public
// entry points only; scripts/reference-pages.ts says which export goes on which page. The pages are build output
// (gitignored there), so they never drift from the code.
//
// The run fails when an export of an entry point is on no page, when a page names something its entry point does not
// export, when an exported symbol or one of its members has no TSDoc summary, or when a function or method lacks an
// @param for one of its parameters. The exemptions are below (exemptMembers, exemptPaths).
//
//   node scripts/generate-sdk-reference.ts
import { execFileSync } from 'node:child_process';
import { mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join, posix, relative } from 'node:path';
import {
  Application,
  DeclarationReflection,
  OptionDefaults,
  ReflectionKind,
  type ProjectReflection,
  type Reflection,
  type TypeDocOptions,
} from 'typedoc';
import type { PluginOptions } from 'typedoc-plugin-markdown';
import ts from 'typescript';
import { entryPoints, pages, tokensFile, type ReferencePage } from './reference-pages.ts';

type Lang = 'en' | 'de';

const website = join(import.meta.dirname, '..');
const repo = join(website, '..');
const work = join(website, 'node_modules', '.cache', 'pithyx-reference');
const docsRoot: Record<Lang, string> = {
  en: join(website, 'src/content/docs/docs/reference/sdk'),
  de: join(website, 'src/content/docs/de/docs/reference/sdk'),
};
const blockTags = OptionDefaults.blockTags;
// Members of generated declarations carry what their generator gives them: the manifest types the schema's
// descriptions, the CSS Module maps of @pithyx/ui/native only class names.
const exemptMembers = ['sdk/src/manifest.generated.ts', 'packages/ui/.css-types/'];
// The wire shape of each entry of Methods; the entry itself is documented, and its parameters on the PithyxApp method.
const exemptPaths = /\.Methods\..*\.__type\.(params|result)(\.|$)/;
// The generated files sit next to this .gitignore, which keeps them out of the repository.
const keep = new Set(['.gitignore']);

const strings: Record<Lang, Record<string, string>> = {
  en: readJson(join(website, 'src/content/i18n/en.json')),
  de: readJson(join(website, 'src/content/i18n/de.json')),
};

function readJson(path: string): Record<string, string> {
  return JSON.parse(readFileSync(path, 'utf8')) as Record<string, string>;
}

function text(lang: Lang, key: string): string {
  const value = strings[lang][key];
  if (value === undefined) {
    throw new Error(`src/content/i18n/${lang}.json has no ${key}`);
  }
  return value;
}

function pageKey(page: { package: string; id: string }): string {
  return `reference.${page.package}.${page.id}`;
}

/** The module path of a page inside the work folder and in TypeDoc's output: `sdk/app`, `ui/button`. */
function modulePath(page: ReferencePage): string {
  return `${page.package}/${page.id}`;
}

/** The site path of a page: sdk pages directly under reference/sdk/, ui pages under reference/sdk/ui/. */
function sitePath(lang: Lang, page: { package: string; id: string }): string {
  const prefix = lang === 'de' ? '/de' : '';
  return page.package === 'sdk'
    ? `${prefix}/docs/reference/sdk/${page.id}/`
    : `${prefix}/docs/reference/sdk/ui/${page.id}/`;
}

/** The exports of every public entry point, by specifier, read with the TypeScript compiler. */
function publicExports(): Map<string, Set<string>> {
  const program = ts.createProgram(
    entryPoints.map((entry) => join(repo, entry.file)),
    {
      ...compilerOptions(),
      noEmit: true,
    },
  );
  const checker = program.getTypeChecker();
  const result = new Map<string, Set<string>>();
  for (const entry of entryPoints) {
    const source = program.getSourceFile(join(repo, entry.file));
    const symbol = source && checker.getSymbolAtLocation(source);
    if (!symbol) {
      throw new Error(`${entry.file} is not a module`);
    }
    result.set(entry.specifier, new Set(checker.getExportsOfModule(symbol).map((s) => s.name)));
  }
  return result;
}

function compilerOptions(): ts.CompilerOptions {
  return {
    target: ts.ScriptTarget.ES2023,
    module: ts.ModuleKind.ESNext,
    moduleResolution: ts.ModuleResolutionKind.Bundler,
    customConditions: ['pithyx-source'],
    lib: ['lib.es2023.d.ts', 'lib.dom.d.ts', 'lib.dom.iterable.d.ts'],
    jsx: ts.JsxEmit.ReactJSX,
    strict: true,
    resolveJsonModule: true,
    skipLibCheck: true,
    rootDirs: [join(repo, 'packages/ui/src'), join(repo, 'packages/ui/.css-types/src')],
  };
}

/** Checks that every public export is on exactly one page and every name on a page is a public export. */
function checkCoverage(exports: Map<string, Set<string>>): string[] {
  const problems: string[] = [];
  const placed = new Map<string, string>();
  for (const page of pages) {
    for (const [specifier, names] of Object.entries(page.exports)) {
      const available = exports.get(specifier);
      if (!available) {
        problems.push(`${modulePath(page)}: ${specifier} is not an entry point`);
        continue;
      }
      for (const name of names === '*' ? available : names) {
        if (!available.has(name)) {
          problems.push(`${modulePath(page)}: ${specifier} does not export ${name}`);
        }
        const key = `${specifier} ${name}`;
        const other = placed.get(key);
        if (other !== undefined) {
          problems.push(`${specifier} ${name} is on ${other} and ${modulePath(page)}`);
        }
        placed.set(key, modulePath(page));
      }
    }
  }
  for (const [specifier, names] of exports) {
    for (const name of names) {
      if (!placed.has(`${specifier} ${name}`)) {
        problems.push(
          `${specifier} exports ${name}, which no page in scripts/reference-pages.ts lists`,
        );
      }
    }
  }
  return problems;
}

/** One entry module per page, re-exporting its names from the package's public entry point. */
function writeEntryModules(): string[] {
  rmSync(work, { recursive: true, force: true });
  const files: string[] = [];
  const ordered = [...pages].sort(
    (a, b) =>
      Number(Object.values(a.exports).includes('*')) -
      Number(Object.values(b.exports).includes('*')),
  );
  for (const page of ordered) {
    const file = join(work, 'entries', `${modulePath(page)}.ts`);
    mkdirSync(dirname(file), { recursive: true });
    const lines = Object.entries(page.exports).map(([specifier, names]) => {
      const entry = entryPoints.find(
        (candidate) => candidate.file && candidate.specifier === specifier,
      );
      const target = posix.join(
        ...relative(dirname(file), join(repo, entry?.file ?? '')).split(/[\\/]/),
      );
      const from = target.replace(/\.tsx?$/, '');
      return names === '*'
        ? `export * from '${from}';`
        : `export { ${names.join(', ')} } from '${from}';`;
    });
    writeFileSync(file, `${lines.join('\n')}\n`);
    files.push(file);
  }
  const tsconfig = {
    compilerOptions: {
      target: 'ES2023',
      module: 'ESNext',
      moduleResolution: 'bundler',
      customConditions: ['pithyx-source'],
      lib: ['ES2023', 'DOM', 'DOM.Iterable'],
      jsx: 'react-jsx',
      strict: true,
      resolveJsonModule: true,
      skipLibCheck: true,
      noEmit: true,
      rootDirs: [join(repo, 'packages/ui/src'), join(repo, 'packages/ui/.css-types/src')],
    },
    include: [
      'entries',
      join(repo, 'packages/ui/.css-types'),
      join(repo, 'packages/ui/src/tabler-icons.d.ts'),
    ],
  };
  writeFileSync(join(work, 'tsconfig.json'), JSON.stringify(tsconfig, null, 2));
  return files;
}

/** The file that declares a reflection; sources are off in the output, the symbol ids still know it. */
function sourceFile(project: ProjectReflection, reflection: Reflection): string | undefined {
  const file = project.getSymbolIdFromReflection(reflection)?.fileName;
  return file === undefined ? undefined : relative(repo, file);
}

function hasSummary(reflection: Reflection): boolean {
  const comment =
    reflection.comment ??
    (reflection instanceof DeclarationReflection
      ? reflection.signatures?.find((signature) => signature.comment)?.comment
      : undefined);
  return (comment?.summary.some((part) => part.text.trim() !== '') ?? false) || false;
}

/** Every exported symbol and member needs a summary, and every parameter of a function or method an @param. */
function checkDocumentation(project: ProjectReflection): string[] {
  const problems: string[] = [];
  const kinds =
    ReflectionKind.Variable |
    ReflectionKind.Function |
    ReflectionKind.Class |
    ReflectionKind.Interface |
    ReflectionKind.TypeAlias |
    ReflectionKind.Property |
    ReflectionKind.Method |
    ReflectionKind.Accessor |
    ReflectionKind.Constructor |
    ReflectionKind.Enum |
    ReflectionKind.EnumMember;
  for (const reflection of Object.values(project.reflections)) {
    if (!(reflection instanceof DeclarationReflection) || !reflection.kindOf(kinds)) {
      continue;
    }
    const file = sourceFile(project, reflection);
    const topLevel = reflection.parent?.kindOf(ReflectionKind.Module) ?? false;
    const name = reflection.getFriendlyFullName();
    if (
      file === undefined ||
      (!topLevel && exemptMembers.some((prefix) => file.startsWith(prefix))) ||
      exemptPaths.test(name)
    ) {
      continue;
    }
    // Inherited members are documented where they are declared (for the components, in React Aria).
    if (reflection.inheritedFrom !== undefined || file.includes('node_modules')) {
      continue;
    }
    if (!hasSummary(reflection)) {
      problems.push(`${file}: ${name} has no TSDoc summary`);
    }
    for (const signature of reflection.signatures ?? []) {
      const tags = new Set(
        (signature.comment ?? reflection.comment)?.blockTags
          .filter((tag) => tag.tag === '@param')
          .map((tag) => tag.name),
      );
      for (const parameter of signature.parameters ?? []) {
        const documented = tags.has(parameter.name) || (parameter.comment?.summary.length ?? 0) > 0;
        if (!documented) {
          problems.push(`${file}: ${name} has no @param for ${parameter.name}`);
        }
      }
    }
  }
  return problems.sort();
}

/** TypeDoc leaves out what it cannot document, such as a re-export from another package; every listed name must show. */
function checkPlaced(project: ProjectReflection): string[] {
  const problems: string[] = [];
  for (const page of pages) {
    const module = project.children?.find((child) => child.name === modulePath(page));
    const shown = new Set((module?.children ?? []).map((child) => child.name));
    for (const [specifier, names] of Object.entries(page.exports)) {
      for (const name of names === '*' ? [] : names) {
        if (!shown.has(name)) {
          problems.push(
            `${modulePath(page)}: ${specifier} ${name} is missing from the generated page`,
          );
        }
      }
    }
  }
  return problems;
}

async function render(lang: Lang, files: string[]): Promise<Map<string, string>> {
  const out = join(work, `out-${lang}`);
  const options: Partial<TypeDocOptions & PluginOptions> = {
    entryPoints: files,
    entryPointStrategy: 'resolve',
    tsconfig: join(work, 'tsconfig.json'),
    plugin: ['typedoc-plugin-markdown'],
    out,
    lang,
    readme: 'none',
    excludePrivate: true,
    excludeExternals: true,
    excludeNotDocumented: false,
    disableSources: true,
    sort: ['source-order'],
    outputFileStrategy: 'modules',
    entryFileName: 'index',
    hidePageHeader: true,
    hideBreadcrumbs: true,
    hidePageTitle: true,
    useCodeBlocks: true,
    expandObjects: false,
    parametersFormat: 'table',
    interfacePropertiesFormat: 'list',
    typeDeclarationFormat: 'list',
    // JSON Schema keywords that json-schema-to-typescript copies into the manifest types.
    blockTags: [...blockTags, '@minItems', '@maxItems'],
    validation: { notExported: false, invalidLink: true, notDocumented: false },
    treatWarningsAsErrors: true,
    logLevel: 'Warn',
  };
  const app = await Application.bootstrapWithPlugins(options);
  const project = await app.convert();
  if (!project) {
    throw new Error('TypeDoc could not convert the packages');
  }
  if (lang === 'en') {
    const problems = [...checkPlaced(project), ...checkDocumentation(project)];
    if (problems.length > 0) {
      throw new Error(`Exported symbols without TSDoc:\n${problems.join('\n')}`);
    }
  }
  app.validate(project);
  if (app.logger.hasErrors() || app.logger.hasWarnings()) {
    throw new Error('TypeDoc reported problems; see above');
  }
  await app.generateOutputs(project);
  const result = new Map<string, string>();
  for (const page of pages) {
    result.set(modulePath(page), readFileSync(join(out, `${modulePath(page)}.md`), 'utf8'));
  }
  return result;
}

/** Every error code the site knows: the daemon's (i18n/en/errors.json) and the SDK's transport codes. */
function errorCodes(): Set<string> {
  const codes = new Set<string>();
  const walk = (tree: Record<string, unknown>, prefix: string[]) => {
    for (const [key, value] of Object.entries(tree)) {
      if (typeof value === 'string') {
        codes.add([...prefix, key].join('.'));
      } else if (typeof value === 'object' && value !== null) {
        walk(value as Record<string, unknown>, [...prefix, key]);
      }
    }
  };
  walk(
    JSON.parse(readFileSync(join(repo, 'i18n/en/errors.json'), 'utf8')) as Record<string, unknown>,
    [],
  );
  return codes;
}

/**
 * TypeDoc's Markdown made into a site page: links between the generated files become site paths, error codes in
 * backticks link to the error reference, and code blocks stay as they are.
 */
function postProcess(
  lang: Lang,
  page: ReferencePage,
  markdown: string,
  codes: Set<string>,
): string {
  const prefix = lang === 'de' ? '/de' : '';
  const here = posix.dirname(modulePath(page));
  const lines: string[] = [];
  let fence = false;
  for (const line of markdown.split('\n')) {
    if (/^\s*(```|~~~)/.test(line)) {
      fence = !fence;
      lines.push(line);
      continue;
    }
    if (fence) {
      lines.push(line);
      continue;
    }
    let result = line.replace(
      /\]\(([^)\s#]*\.md)(#[^)\s]*)?\)/g,
      (match, target: string, hash = '') => {
        if (/^[a-z]+:/.test(target)) {
          return match;
        }
        const resolved = posix.normalize(posix.join(here, target)).replace(/\.md$/, '');
        const linked = pages.find((candidate) => modulePath(candidate) === resolved);
        if (!linked) {
          throw new Error(`${modulePath(page)} links to ${target}, which is no page`);
        }
        return `](${sitePath(lang, linked)}${String(hash).toLowerCase()})`;
      },
    );
    if (!line.startsWith('#')) {
      result = result.replace(
        /(\[)?`([a-z][a-z0-9_]*(?:\.[a-z0-9_]+){2,})`(\])?/g,
        (match, open, code: string, close) => {
          if (open !== undefined || close !== undefined || !codes.has(code)) {
            return match;
          }
          return `[\`${code}\`](${prefix}/docs/reference/errors/#${code})`;
        },
      );
    }
    lines.push(result);
  }
  return lines
    .join('\n')
    .replace(/\n{3,}/g, '\n\n')
    .trim();
}

function frontmatter(title: string, description: string, order: number): string {
  const quote = (value: string) => `'${value.replace(/'/g, "''")}'`;
  return [
    '---',
    `title: ${quote(title)}`,
    `description: ${quote(description)}`,
    'status: ready',
    'audience: developer',
    // Generated pages have no file in the repository to edit and no commit date.
    'editUrl: false',
    'lastUpdated: false',
    'sidebar:',
    `  order: ${String(order)}`,
    '---',
    '',
  ].join('\n');
}

function packageVersion(name: 'sdk' | 'ui'): string {
  const file = name === 'sdk' ? 'sdk/package.json' : 'packages/ui/package.json';
  return (JSON.parse(readFileSync(join(repo, file), 'utf8')) as { version: string }).version;
}

function generatedNote(lang: Lang, name: 'sdk' | 'ui'): string {
  return text(lang, 'reference.generated')
    .replace('{package}', `@pithyx/${name}`)
    .replace('{version}', packageVersion(name));
}

interface Token {
  name: string;
  value: string;
  /** A comment right above the declaration. */
  note?: string;
}

interface TokenGroup {
  /** The first sentence of the comment that opens the group. */
  title: string;
  /** The rest of that comment. */
  text: string;
  tokens: Token[];
}

interface TokenFile {
  groups: TokenGroup[];
  accents: { name: string; tokens: Token[] }[];
  overrides: { condition: string; tokens: Token[] }[];
}

/**
 * The tokens of tokens.css: the main `:root` block in groups (a comment after a blank line opens a group, a comment
 * right above a declaration annotates it), the accent presets and the `@media` overrides.
 */
function readTokens(): TokenFile {
  const css = readFileSync(join(repo, tokensFile), 'utf8');
  const declarations = (body: string): Token[] =>
    [...body.matchAll(/(--[\w-]+)\s*:\s*([^;]+);/g)].map((match) => ({
      name: match[1] ?? '',
      value: (match[2] ?? '').replace(/\s+/g, ' ').trim(),
    }));
  const overrides = [...css.matchAll(/@media ([^{]+)\{\s*:root\s*\{([^}]*)\}\s*\}/g)].map(
    (match) => ({ condition: (match[1] ?? '').trim(), tokens: declarations(match[2] ?? '') }),
  );
  const topLevel = css.replace(/@media [^{]+\{\s*:root\s*\{[^}]*\}\s*\}/g, '');
  const accents: TokenFile['accents'] = [];
  const groups: TokenGroup[] = [];
  for (const block of topLevel.matchAll(/([^{}]*)\{([^{}]*)\}/g)) {
    const selector = (block[1] ?? '').replace(/\/\*[\s\S]*?\*\//g, '').trim();
    const body = block[2] ?? '';
    const accent = /\[data-accent='(\w+)'\]/.exec(selector);
    if (accent) {
      accents.push({ name: accent[1] ?? '', tokens: declarations(body) });
      continue;
    }
    if (selector !== ':root' || !body.includes('--')) {
      continue;
    }
    let current: TokenGroup | undefined;
    let note: string | undefined;
    let blank = true;
    for (const line of joinDeclarations(body.split('\n'))) {
      if (line.trim() === '') {
        blank = true;
        continue;
      }
      const comment = /^\s*\/\*\s*(.*?)\s*\*\/\s*$/.exec(line)?.[1];
      if (comment !== undefined) {
        if (blank || !current) {
          const [title = '', ...rest] = comment.split(/(?<=\.)\s+/);
          current = { title: title.replace(/\.$/, ''), text: rest.join(' '), tokens: [] };
          groups.push(current);
        } else {
          note = comment;
        }
        blank = false;
        continue;
      }
      blank = false;
      const declaration = /^\s*(--[\w-]+)\s*:\s*(.+);\s*$/.exec(line);
      if (declaration && current) {
        current.tokens.push({
          name: declaration[1] ?? '',
          value: (declaration[2] ?? '').trim(),
          ...(note === undefined ? {} : { note }),
        });
        note = undefined;
      }
    }
  }
  return { groups: groups.filter((group) => group.tokens.length > 0), accents, overrides };
}

/** Joins a declaration that Prettier broke over several lines (a long `light-dark()`) into one line. */
function joinDeclarations(lines: string[]): string[] {
  const joined: string[] = [];
  let open: string | undefined;
  for (const line of lines) {
    if (open !== undefined) {
      const part = line.trim();
      open = `${open}${open.endsWith('(') || part.startsWith(')') ? '' : ' '}${part}`;
      if (open.endsWith(';')) {
        joined.push(open);
        open = undefined;
      }
      continue;
    }
    if (/^\s*--[\w-]+\s*:/.test(line) && !line.trimEnd().endsWith(';')) {
      open = line.trimEnd();
      continue;
    }
    joined.push(line);
  }
  return joined;
}

function tokenTable(lang: Lang, tokens: Token[]): string {
  const notes = tokens.some((token) => token.note !== undefined);
  const cell = (value: string) => value.replace(/\|/g, '\\|');
  const head = [text(lang, 'reference.tokens.token'), text(lang, 'reference.tokens.value')];
  if (notes) {
    head.push(text(lang, 'reference.tokens.note'));
  }
  return [
    `| ${head.join(' | ')} |`,
    `|${' --- |'.repeat(head.length)}`,
    ...tokens.map(
      (token) =>
        `| \`${token.name}\` | \`${cell(token.value)}\` |${notes ? ` ${cell(token.note ?? '')} |` : ''}`,
    ),
  ].join('\n');
}

function tokensPage(lang: Lang): string {
  const { groups, accents, overrides } = readTokens();
  return [
    text(lang, 'reference.ui.tokens.intro'),
    text(lang, 'reference.generatedTokens')
      .replace('{file}', `\`${tokensFile}\``)
      .replace('{version}', packageVersion('ui')),
    `## ${text(lang, 'reference.tokens.semantic')}`,
    ...groups.map((group) =>
      [`### ${group.title}`, group.text, tokenTable(lang, group.tokens)]
        .filter((part) => part !== '')
        .join('\n\n'),
    ),
    `## ${text(lang, 'reference.tokens.accents')}`,
    text(lang, 'reference.tokens.accentsIntro'),
    ...accents.map((accent) => `### \`${accent.name}\`\n\n${tokenTable(lang, accent.tokens)}`),
    `## ${text(lang, 'reference.tokens.overrides')}`,
    ...overrides.map(
      (override) => `### \`${override.condition}\`\n\n${tokenTable(lang, override.tokens)}`,
    ),
  ].join('\n\n');
}

function indexPage(lang: Lang): string {
  const list = (name: 'sdk' | 'ui') =>
    [
      ...pages
        .filter((page) => page.package === name)
        .map(
          (page) =>
            `- [${text(lang, `${pageKey(page)}.title`)}](${sitePath(lang, page)}): ${text(lang, `${pageKey(page)}.description`)}`,
        ),
      ...(name === 'ui'
        ? [
            `- [${text(lang, 'reference.ui.tokens.title')}](${sitePath(lang, { package: 'ui', id: 'tokens' })}): ${text(lang, 'reference.ui.tokens.description')}`,
          ]
        : []),
    ].join('\n');
  return [
    text(lang, 'reference.index.intro'),
    `## @pithyx/sdk`,
    list('sdk'),
    `## @pithyx/ui`,
    list('ui'),
  ].join('\n\n');
}

function clean(folder: string) {
  mkdirSync(folder, { recursive: true });
  for (const name of readdirSync(folder)) {
    if (!keep.has(name)) {
      rmSync(join(folder, name), { recursive: true, force: true });
    }
  }
}

function write(path: string, content: string) {
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, `${content.trim()}\n`);
}

async function main() {
  // @pithyx/ui imports CSS Modules, whose declarations live in the gitignored .css-types folder.
  execFileSync(process.execPath, [join(repo, 'tools/css-module-types.ts'), 'packages/ui'], {
    cwd: repo,
    stdio: 'inherit',
  });

  const coverage = checkCoverage(publicExports());
  if (coverage.length > 0) {
    throw new Error(`The reference pages do not match the public exports:\n${coverage.join('\n')}`);
  }
  // TypeDoc documents a symbol on the first page that exports it and links to it from the others, so pages that take
  // a whole entry point (host re-exports PithyxError) come last.
  const files = writeEntryModules();
  const codes = errorCodes();
  let count = 0;
  for (const lang of ['en', 'de'] as const) {
    const rendered = await render(lang, files);
    clean(docsRoot[lang]);
    write(
      join(docsRoot[lang], 'index.md'),
      frontmatter(
        text(lang, 'reference.index.title'),
        text(lang, 'reference.index.description'),
        10,
      ) + indexPage(lang),
    );
    pages.forEach((page, index) => {
      const key = pageKey(page);
      const body = postProcess(lang, page, rendered.get(modulePath(page)) ?? '', codes);
      const folder = page.package === 'sdk' ? docsRoot[lang] : join(docsRoot[lang], 'ui');
      write(
        join(folder, `${page.id}.md`),
        frontmatter(text(lang, `${key}.title`), text(lang, `${key}.description`), 11 + index) +
          [text(lang, `${key}.intro`), generatedNote(lang, page.package), body].join('\n\n'),
      );
      count++;
    });
    write(
      join(docsRoot[lang], 'ui', 'tokens.md'),
      frontmatter(
        text(lang, 'reference.ui.tokens.title'),
        text(lang, 'reference.ui.tokens.description'),
        11 + pages.length,
      ) + tokensPage(lang),
    );
    count += 2;
  }
  console.log(`reference: ${String(count)} pages in reference/sdk/ (en and de)`);
}

await main();
