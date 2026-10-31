// Writes the generated parts of the reference pages, so they never drift from their sources: the pithyx commands from
// packages/cli/src/cli.ts (its help text and its parseArgs options), the findings from packages/cli/src/issues.ts and
// findings.ts, the manifest fields from sdk/schema/manifest.v2.schema.json and the Compose allowlist from
// sdk/schema/compose.v1.rules.json. A page marks each part with `<!-- generated:<kind> [argument] -->` and
// `<!-- /generated -->`; everything else on the page is written by hand. Run by build:site before the build.
// Usage: node scripts/generate-cli-reference.ts [--check]   --check rewrites nothing and fails when a page is stale.
import { readdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { join, relative } from 'node:path';
import { format, resolveConfig } from 'prettier';
import ts from 'typescript';

const repo = join(import.meta.dirname, '..', '..');
const cliSource = join(repo, 'packages', 'cli', 'src');
const docs = join(repo, 'website', 'src', 'content', 'docs');

type Locale = 'en' | 'de';

const labels = {
  en: {
    option: 'Option',
    value: 'Value',
    type: 'Type',
    default: 'Default',
    description: 'Description',
    command: 'Command',
    required: 'required',
    yes: 'yes',
    no: 'no',
    off: 'off',
    none: '–',
    alternative: 'see the forms below',
    repeatable: 'repeatable',
    noHelp: 'Not described in `pithyx --help`; see below.',
    noOptions: 'This command has no options of its own.',
    field: 'Field',
    requiredColumn: 'Required',
    allowed: 'Allowed values',
    key: 'Key',
    limits: 'Limits',
    right: 'Elevated right',
    reason: 'Why it is refused',
    name: 'Name',
    pattern: 'Pattern',
    error: 'error',
    warning: 'warning',
    longPattern: 'a pattern of {n} characters, see the schema',
    chars: '{min} to {max} characters',
    atMostChars: 'at most {max} characters',
    atLeastChars: 'at least {min} characters',
    items: '{min} to {max} entries',
    atMostItems: 'at most {max} entries',
    atLeastItems: 'at least {min} entries',
    atMostProperties: 'at most {max} entries',
    unique: 'no duplicates',
    range: '{min} to {max}',
    atLeast: 'at least {min}',
    atMost: 'at most {max}',
    arrayOf: 'array of {type}',
    keyName: 'Keys matching',
    sameAs: 'Each one is like {field}.',
    eachEntry: 'Each entry:',
    usesRight: 'using the key needs it',
  },
  de: {
    option: 'Option',
    value: 'Wert',
    type: 'Typ',
    default: 'Standard',
    description: 'Beschreibung',
    command: 'Befehl',
    required: 'Pflicht',
    yes: 'ja',
    no: 'nein',
    off: 'aus',
    none: '–',
    alternative: 'siehe die Formen unten',
    repeatable: 'wiederholbar',
    noHelp: 'In `pithyx --help` nicht beschrieben; siehe unten.',
    noOptions: 'Dieser Befehl hat keine eigenen Optionen.',
    field: 'Feld',
    requiredColumn: 'Pflicht',
    allowed: 'Erlaubte Werte',
    key: 'Schlüssel',
    limits: 'Grenzen',
    right: 'Erweitertes Recht',
    reason: 'Warum er abgelehnt wird',
    name: 'Name',
    pattern: 'Muster',
    error: 'Fehler',
    warning: 'Warnung',
    longPattern: 'ein Muster aus {n} Zeichen, siehe das Schema',
    chars: '{min} bis {max} Zeichen',
    atMostChars: 'höchstens {max} Zeichen',
    atLeastChars: 'mindestens {min} Zeichen',
    items: '{min} bis {max} Einträge',
    atMostItems: 'höchstens {max} Einträge',
    atLeastItems: 'mindestens {min} Einträge',
    atMostProperties: 'höchstens {max} Einträge',
    unique: 'keine Duplikate',
    range: '{min} bis {max}',
    atLeast: 'mindestens {min}',
    atMost: 'höchstens {max}',
    arrayOf: 'Array aus {type}',
    keyName: 'Schlüssel passend zu',
    sameAs: 'Jeder ist wie {field}.',
    eachEntry: 'Jeder Eintrag:',
    usesRight: 'der Schlüssel braucht es',
  },
} satisfies Record<Locale, Record<string, string>>;

type Label = keyof (typeof labels)['en'];

function t(locale: Locale, key: Label, params: Record<string, string | number> = {}): string {
  return labels[locale][key].replace(/\{(\w+)\}/g, (_, name: string) => String(params[name] ?? ''));
}

/** Notes for the report: what the sources do not describe. */
const report = {
  optionsWithoutHelp: new Set<string>(),
  commandsWithoutHelp: new Set<string>(),
  fieldsWithoutDescription: new Set<string>(),
  rulesWithoutDescription: new Set<string>(),
  findingsWithoutLevel: new Set<string>(),
};

/** Text for Markdown: `<` and `>` outside code spans become entities, so `<id>` stays visible. */
function prose(text: string): string {
  return text
    .split(/(`+[^`]*`+)/)
    .map((part, index) =>
      index % 2 === 1 ? part : part.replace(/</g, '&lt;').replace(/>/g, '&gt;'),
    )
    .join('');
}

function capitalized(text: string): string {
  return text.charAt(0).toUpperCase() + text.slice(1);
}

function cell(text: string): string {
  return text.replace(/\s+/g, ' ').replace(/\|/g, '\\|').trim();
}

function code(text: string): string {
  const fence = text.includes('`') ? '``' : '`';
  return `${fence}${text}${fence}`;
}

function table(header: string[], rows: string[][]): string {
  return [
    `| ${header.join(' | ')} |`,
    `| ${header.map(() => '---').join(' | ')} |`,
    ...rows.map((row) => `| ${row.map(cell).join(' | ')} |`),
  ].join('\n');
}

function docsLink(locale: Locale, path: string): string {
  return `${locale === 'de' ? '/de' : ''}/docs/${path}`;
}

// The CLI: its help text and its parseArgs options are the command definitions.

interface ParsedOption {
  name: string;
  type: 'string' | 'boolean';
  multiple: boolean;
  short?: string;
}

interface HelpOption {
  value?: string;
  scope?: string;
  text: string;
  default?: string;
}

interface CliCommand {
  /** `validate`, or `catalog check` for a catalog subcommand. */
  name: string;
  summary: string;
  /** The usage line of the help text, for catalog subcommands. */
  usage?: string;
  /** The options its code reads, in parseArgs order. */
  options: string[];
}

interface Cli {
  options: ParsedOption[];
  help: Map<string, HelpOption>;
  commands: CliCommand[];
  /** The command line and the first lines of the help text. */
  usage: string;
}

function readCli(): Cli {
  const path = join(cliSource, 'cli.ts');
  const source = ts.createSourceFile(
    path,
    readFileSync(path, 'utf8'),
    ts.ScriptTarget.Latest,
    true,
  );
  let helpText = '';
  let parseArgsOptions: ts.ObjectLiteralExpression | undefined;
  const functions = new Map<string, ts.FunctionDeclaration>();
  const visit = (node: ts.Node) => {
    if (
      ts.isVariableDeclaration(node) &&
      ts.isIdentifier(node.name) &&
      node.name.text === 'HELP' &&
      node.initializer !== undefined
    ) {
      helpText = node.initializer
        .getText(source)
        .slice(1, -1)
        .replace(/\$\{(\w+)\}/g, '<$1>');
    }
    if (
      ts.isCallExpression(node) &&
      ts.isIdentifier(node.expression) &&
      node.expression.text === 'parseArgs'
    ) {
      const [argument] = node.arguments;
      if (argument !== undefined && ts.isObjectLiteralExpression(argument)) {
        const options = property(argument, 'options');
        if (options !== undefined && ts.isObjectLiteralExpression(options)) {
          parseArgsOptions = options;
        }
      }
    }
    if (ts.isFunctionDeclaration(node) && node.name !== undefined) {
      functions.set(node.name.text, node);
    }
    ts.forEachChild(node, visit);
  };
  visit(source);
  if (helpText === '' || parseArgsOptions === undefined) {
    throw new Error('packages/cli/src/cli.ts has no HELP text or no parseArgs options');
  }

  const options = parseArgsOptions.properties.flatMap((member): ParsedOption[] => {
    if (!ts.isPropertyAssignment(member) || !ts.isObjectLiteralExpression(member.initializer)) {
      return [];
    }
    const name = ts.isStringLiteral(member.name) ? member.name.text : member.name.getText(source);
    const literal = (key: string) => {
      const value = property(member.initializer as ts.ObjectLiteralExpression, key);
      return value === undefined ? undefined : value.getText(source).replace(/^'|'$/g, '');
    };
    const short = literal('short');
    return [
      {
        name,
        type: literal('type') === 'boolean' ? 'boolean' : 'string',
        multiple: literal('multiple') === 'true',
        ...(short === undefined ? {} : { short }),
      },
    ];
  });

  const sections = helpSections(helpText);
  const help = new Map<string, HelpOption>();
  const commandNames = new Set(sections.commands.map(([name]) => name));
  for (const line of sections.options) {
    const match = /^(?:-\w, )?--([\w-]+)(?: (<[^>]+>))?\s+(.*)$/.exec(line);
    if (match === null) {
      continue;
    }
    const [, name = '', value, rest = ''] = match;
    const scoped = /^(\w+): (.*)$/.exec(rest);
    const scope = scoped !== null && commandNames.has(scoped[1] ?? '') ? scoped[1] : undefined;
    let text = scope === undefined ? rest : (scoped?.[2] ?? rest);
    const fallback = /\s*\(default: ([^)]+)\)/.exec(text);
    if (fallback !== null) {
      text = text.replace(fallback[0], '');
    }
    help.set(name, {
      text: text.trim(),
      ...(value === undefined ? {} : { value }),
      ...(scope === undefined ? {} : { scope }),
      ...(fallback?.[1] === undefined ? {} : { default: fallback[1] }),
    });
  }

  const commands: CliCommand[] = [];
  const main = functions.get('main');
  const catalog = functions.get('catalog');
  if (main === undefined || catalog === undefined) {
    throw new Error('packages/cli/src/cli.ts has no main() or catalog()');
  }
  const names = options.map((option) => option.name);
  const helpOf = new Map(sections.commands);
  for (const [name, used] of caseOptions(main, names)) {
    const summary = helpOf.get(name);
    if (summary === undefined) {
      report.commandsWithoutHelp.add(name);
    }
    commands.push({ name, summary: summary ?? '', options: used });
  }
  for (const [name, used] of caseOptions(catalog, names)) {
    const entry = sections.catalog.find((sub) => sub.name === name);
    if (entry === undefined) {
      report.commandsWithoutHelp.add(`catalog ${name}`);
    }
    commands.push({
      name: `catalog ${name}`,
      summary: entry?.summary ?? '',
      ...(entry === undefined ? {} : { usage: entry.usage }),
      options: used,
    });
  }
  for (const command of commands) {
    for (const option of command.options) {
      if (!help.has(option)) {
        report.optionsWithoutHelp.add(`--${option} (${command.name})`);
      }
    }
  }
  return { options, help, commands, usage: sections.usage };
}

function property(object: ts.ObjectLiteralExpression, name: string): ts.Expression | undefined {
  for (const member of object.properties) {
    if (
      ts.isPropertyAssignment(member) &&
      (ts.isIdentifier(member.name) || ts.isStringLiteral(member.name)) &&
      member.name.text === name
    ) {
      return member.initializer;
    }
  }
  return undefined;
}

interface HelpSections {
  usage: string;
  commands: [string, string][];
  catalog: { name: string; usage: string; summary: string }[];
  options: string[];
}

/** Splits the help text into its usage line, commands (with the catalog subcommands) and option lines. */
function helpSections(text: string): HelpSections {
  const lines = text.split('\n');
  const result: HelpSections = { usage: '', commands: [], catalog: [], options: [] };
  let section: 'head' | 'commands' | 'options' | 'tail' = 'head';
  let catalogPart: 'usage' | 'summary' | undefined;
  for (const line of lines) {
    if (line === 'Commands:') {
      section = 'commands';
      continue;
    }
    if (line === 'Options:') {
      section = 'options';
      continue;
    }
    if (line.trim() === '') {
      if (section !== 'head') {
        section = section === 'commands' || section === 'options' ? 'tail' : section;
      }
      continue;
    }
    if (section === 'head' && line.startsWith('Usage: ')) {
      result.usage = line.slice('Usage: '.length);
    } else if (section === 'commands') {
      const start = /^ {2}(\w+)\s{2,}(.*)$/.exec(line);
      const last = result.commands.at(-1);
      const trimmed = line.trim();
      if (start !== null) {
        result.commands.push([start[1] ?? '', start[2] ?? '']);
        catalogPart = undefined;
      } else if (last?.[0] === 'catalog' && trimmed.startsWith('catalog ')) {
        const [, name = '', ...rest] = trimmed.split(' ');
        result.catalog.push({
          name,
          usage: `catalog ${name} ${rest.join(' ')}`.trim(),
          summary: '',
        });
        catalogPart = 'usage';
      } else if (last?.[0] === 'catalog' && catalogPart === 'usage' && /^[-(|]/.test(trimmed)) {
        const sub = result.catalog.at(-1);
        if (sub !== undefined) {
          sub.usage += ` ${trimmed}`;
        }
      } else if (last?.[0] === 'catalog' && catalogPart !== undefined) {
        const sub = result.catalog.at(-1);
        if (sub !== undefined) {
          sub.summary = `${sub.summary} ${trimmed}`.trim();
        }
        catalogPart = 'summary';
      } else if (last !== undefined) {
        last[1] = `${last[1]} ${trimmed}`;
      }
    } else if (section === 'options') {
      if (/^ {2}-/.test(line)) {
        result.options.push(line.trim());
      } else if (result.options.length > 0) {
        const last = result.options.length - 1;
        result.options[last] = `${result.options[last] ?? ''} ${line.trim()}`;
      }
    }
  }
  return result;
}

/** The options each `case` of the function's switch reads: `values.x`, `values['x']`, `text('x')`, `list('x')`. */
function caseOptions(fn: ts.FunctionDeclaration, names: readonly string[]): [string, string[]][] {
  const direct = (node: ts.Node, locals: Map<string, Set<string>>): Set<string> => {
    const found = new Set<string>();
    const visit = (child: ts.Node) => {
      if (
        ts.isPropertyAccessExpression(child) &&
        ts.isIdentifier(child.expression) &&
        child.expression.text === 'values'
      ) {
        found.add(child.name.text);
      } else if (
        ts.isElementAccessExpression(child) &&
        ts.isIdentifier(child.expression) &&
        child.expression.text === 'values' &&
        ts.isStringLiteral(child.argumentExpression)
      ) {
        found.add(child.argumentExpression.text);
      } else if (
        ts.isCallExpression(child) &&
        ts.isIdentifier(child.expression) &&
        (child.expression.text === 'text' || child.expression.text === 'list')
      ) {
        const [argument] = child.arguments;
        if (argument !== undefined && ts.isStringLiteral(argument)) {
          found.add(argument.text);
        }
      } else if (ts.isIdentifier(child) && locals.has(child.text)) {
        for (const name of locals.get(child.text) ?? []) {
          found.add(name);
        }
      }
      ts.forEachChild(child, visit);
    };
    visit(node);
    return found;
  };
  const locals = new Map<string, Set<string>>();
  const result: [string, string[]][] = [];
  const walk = (node: ts.Node) => {
    if (
      ts.isVariableDeclaration(node) &&
      ts.isIdentifier(node.name) &&
      node.initializer !== undefined &&
      !ts.isArrowFunction(node.initializer)
    ) {
      locals.set(node.name.text, direct(node.initializer, locals));
    }
    if (ts.isCaseClause(node) && ts.isStringLiteral(node.expression)) {
      const used = direct(node, locals);
      result.push([node.expression.text, names.filter((name) => used.has(name))]);
      return;
    }
    ts.forEachChild(node, walk);
  };
  walk(fn);
  return result;
}

let cliCache: Cli | undefined;
function cli(): Cli {
  cliCache ??= readCli();
  return cliCache;
}

function command(name: string): CliCommand {
  const found = cli().commands.find((candidate) => candidate.name === name);
  if (found === undefined) {
    throw new Error(`pithyx has no command ${name}`);
  }
  return found;
}

/** Where an option stands in a usage line: `[--x]` optional, `(--x ...)` one of several forms, else required. */
function placement(
  usage: string,
  option: string,
): 'optional' | 'alternative' | 'required' | 'absent' {
  const at = usage.search(new RegExp(`--${option}(?![\\w-])`));
  if (at === -1) {
    return 'absent';
  }
  let square = 0;
  let round = 0;
  for (const char of usage.slice(0, at)) {
    square += char === '[' ? 1 : char === ']' ? -1 : 0;
    round += char === '(' ? 1 : char === ')' ? -1 : 0;
  }
  return square > 0 ? 'optional' : round > 0 ? 'alternative' : 'required';
}

/** The value an option takes: from the option list of the help, else from the command's usage line. */
function optionValue(option: ParsedOption, usage?: string): string {
  const fromUsage =
    usage === undefined
      ? undefined
      : new RegExp(`--${option.name} (<[^>]+>|[\\w|]+)`).exec(usage)?.[1];
  return cli().help.get(option.name)?.value ?? fromUsage ?? '<value>';
}

function optionUsage(option: ParsedOption): string {
  return option.type === 'boolean' ? `--${option.name}` : `--${option.name} ${optionValue(option)}`;
}

function synopsis(name: string): string {
  const entry = command(name);
  const options = cli().options.filter((option) => entry.options.includes(option.name));
  if (entry.usage !== undefined) {
    const missing = options
      .filter((option) => placement(entry.usage ?? '', option.name) === 'absent')
      .map((option) => `[${optionUsage(option)}]${option.multiple ? '...' : ''}`);
    return ['pithyx', entry.usage, ...missing].join(' ');
  }
  const folder = cli().usage.includes('[project folder]') ? ' [project folder]' : '';
  return [
    `pithyx ${name}${folder}`,
    ...options.map((option) => `[${optionUsage(option)}]${option.multiple ? '...' : ''}`),
  ].join(' ');
}

function optionsTable(name: string, locale: Locale): string {
  const entry = command(name);
  const options = cli().options.filter((option) => entry.options.includes(option.name));
  if (options.length === 0) {
    return t(locale, 'noOptions');
  }
  return table(
    [
      t(locale, 'option'),
      t(locale, 'value'),
      t(locale, 'type'),
      t(locale, 'default'),
      t(locale, 'description'),
    ],
    options.map((option) => {
      const help = cli().help.get(option.name);
      const where = entry.usage === undefined ? 'optional' : placement(entry.usage, option.name);
      const fallback =
        help?.default !== undefined
          ? help.default.includes(' ')
            ? help.default
            : code(help.default)
          : where === 'required'
            ? t(locale, 'required')
            : where === 'alternative'
              ? t(locale, 'alternative')
              : option.type === 'boolean'
                ? t(locale, 'off')
                : t(locale, 'none');
      const value =
        option.type === 'boolean' ? t(locale, 'none') : code(optionValue(option, entry.usage));
      const type = option.multiple ? `${option.type}, ${t(locale, 'repeatable')}` : option.type;
      return [
        `${option.short === undefined ? '' : `${code(`-${option.short}`)}, `}${code(`--${option.name}`)}`,
        value,
        type,
        fallback,
        help === undefined ? t(locale, 'noHelp') : prose(capitalized(help.text)),
      ];
    }),
  );
}

function globalOptionsTable(locale: Locale): string {
  const options = cli().options.filter((option) => option.short !== undefined);
  return table(
    [t(locale, 'option'), t(locale, 'description')],
    options.map((option) => [
      `${code(`-${option.short ?? ''}`)}, ${code(`--${option.name}`)}`,
      prose(cli().help.get(option.name)?.text ?? t(locale, 'noHelp')),
    ]),
  );
}

function commandsTable(locale: Locale): string {
  return table(
    [t(locale, 'command'), t(locale, 'description')],
    cli().commands.map((entry) => [
      `[${code(`pithyx ${entry.name}`)}](${docsLink(locale, `reference/cli/${entry.name.replace(' ', '-')}/`)})`,
      prose(capitalized(entry.summary)),
    ]),
  );
}

// Findings: the codes of issues.ts and findings.ts with their level and the box's or the CLI's message.

interface FindingCode {
  code: string;
  level: 'error' | 'warning' | 'both';
}

function stringUnion(path: string, typeName: string, propertyName?: string): string[] {
  const source = ts.createSourceFile(
    path,
    readFileSync(path, 'utf8'),
    ts.ScriptTarget.Latest,
    true,
  );
  const codes: string[] = [];
  const visit = (node: ts.Node) => {
    const owner =
      (ts.isTypeAliasDeclaration(node) || ts.isInterfaceDeclaration(node)) &&
      node.name.text === typeName;
    if (owner) {
      const target =
        propertyName === undefined
          ? node
          : node
              .getChildren(source)
              .flatMap((child) => child.getChildren(source))
              .find(
                (child) =>
                  ts.isPropertySignature(child) && child.name.getText(source) === propertyName,
              );
      const collect = (child: ts.Node) => {
        if (ts.isLiteralTypeNode(child) && ts.isStringLiteral(child.literal)) {
          codes.push(child.literal.text);
        }
        ts.forEachChild(child, collect);
      };
      if (target !== undefined) {
        collect(target);
      }
    }
    ts.forEachChild(node, visit);
  };
  visit(source);
  return codes;
}

function cliTexts(): Map<string, string> {
  const path = join(cliSource, 'findings.ts');
  const source = ts.createSourceFile(
    path,
    readFileSync(path, 'utf8'),
    ts.ScriptTarget.Latest,
    true,
  );
  const texts = new Map<string, string>();
  const visit = (node: ts.Node) => {
    if (
      ts.isVariableDeclaration(node) &&
      ts.isIdentifier(node.name) &&
      node.name.text === 'cliTexts' &&
      node.initializer !== undefined &&
      ts.isObjectLiteralExpression(node.initializer)
    ) {
      for (const member of node.initializer.properties) {
        if (
          ts.isPropertyAssignment(member) &&
          ts.isStringLiteral(member.name) &&
          ts.isStringLiteral(member.initializer)
        ) {
          texts.set(member.name.text, member.initializer.text);
        }
      }
    }
    ts.forEachChild(node, visit);
  };
  visit(source);
  return texts;
}

/** The level of every finding the CLI's sources write as `{ level, code }`; the others come through `error()`. */
function findingLevels(): Map<string, Set<string>> {
  const levels = new Map<string, Set<string>>();
  for (const name of readdirSync(cliSource, { recursive: true, encoding: 'utf8' })) {
    if (!name.endsWith('.ts') || name.endsWith('.test.ts')) {
      continue;
    }
    const path = join(cliSource, name);
    const source = ts.createSourceFile(
      path,
      readFileSync(path, 'utf8'),
      ts.ScriptTarget.Latest,
      true,
    );
    const visit = (node: ts.Node) => {
      if (ts.isObjectLiteralExpression(node)) {
        const level = property(node, 'level');
        const found = property(node, 'code');
        if (
          level !== undefined &&
          found !== undefined &&
          ts.isStringLiteral(level) &&
          ts.isStringLiteral(found)
        ) {
          levels.set(found.text, (levels.get(found.text) ?? new Set()).add(level.text));
        }
      }
      ts.forEachChild(node, visit);
    };
    visit(source);
  }
  return levels;
}

function findingCodes(): FindingCode[] {
  const issues = stringUnion(join(cliSource, 'issues.ts'), 'Issue', 'code');
  const cliCodes = stringUnion(join(cliSource, 'findings.ts'), 'CliCode');
  const levels = findingLevels();
  return [...issues, ...cliCodes].map((name) => {
    const found = levels.get(name) ?? new Set<string>();
    if (issues.includes(name)) {
      found.add('error');
    }
    if (found.size === 0) {
      report.findingsWithoutLevel.add(name);
    }
    const level = found.size > 1 ? 'both' : found.has('warning') ? 'warning' : 'error';
    return { code: name, level };
  });
}

function boxMessages(locale: Locale): Map<string, string> {
  const tree = JSON.parse(
    readFileSync(join(repo, 'i18n', locale, 'errors.json'), 'utf8'),
  ) as unknown;
  const messages = new Map<string, string>();
  const walk = (node: unknown, prefix: string[]) => {
    if (typeof node === 'string') {
      messages.set(prefix.join('.'), node);
    } else if (typeof node === 'object' && node !== null) {
      for (const [key, value] of Object.entries(node)) {
        walk(value, [...prefix, key]);
      }
    }
  };
  walk(tree, []);
  return messages;
}

/** A message with its top-level ICU selects taken out: the text with `{name}` and each variant on its own. */
function splitSelects(message: string): { text: string; variants: [string, string, string][] } {
  const variants: [string, string, string][] = [];
  let text = '';
  let index = 0;
  while (index < message.length) {
    const char = message[index] ?? '';
    if (char !== '{') {
      text += char;
      index++;
      continue;
    }
    const end = matching(message, index);
    const inner = message.slice(index + 1, end);
    const select = /^\s*(\w+)\s*,\s*(?:select|plural)\s*,([\s\S]*)$/.exec(inner);
    if (select === null) {
      text += message.slice(index, end + 1);
    } else {
      const [, name = '', body = ''] = select;
      text += `{${name}}`;
      let at = 0;
      while (at < body.length) {
        const key = /^\s*([=\w]+)\s*\{/.exec(body.slice(at));
        if (key === null) {
          break;
        }
        const open = at + key[0].length - 1;
        const close = matching(body, open);
        variants.push([name, key[1] ?? '', body.slice(open + 1, close)]);
        at = close + 1;
      }
    }
    index = end + 1;
  }
  return { text, variants };
}

function matching(text: string, open: number): number {
  let depth = 0;
  for (let index = open; index < text.length; index++) {
    depth += text[index] === '{' ? 1 : text[index] === '}' ? -1 : 0;
    if (depth === 0) {
      return index;
    }
  }
  return text.length - 1;
}

function findingsList(prefix: string, locale: Locale): string {
  const box = boxMessages(locale);
  const cliMessages = cliTexts();
  return findingCodes()
    .filter((finding) => finding.code.startsWith(`${prefix}.`))
    .sort((a, b) => a.code.localeCompare(b.code))
    .map((finding) => {
      const message = cliMessages.get(finding.code) ?? box.get(finding.code) ?? '';
      const { text, variants } = splitSelects(message);
      const level =
        finding.level === 'both'
          ? `${t(locale, 'error')}, ${t(locale, 'warning')}`
          : t(locale, finding.level);
      const name = cliMessages.has(finding.code)
        ? code(finding.code)
        : `[${code(finding.code)}](${docsLink(locale, `reference/errors/#${finding.code}`)})`;
      return [
        `- ${name} (${level}): ${prose(text.replace(/\s+/g, ' '))}`,
        ...variants.map(
          ([param, key, variant]) =>
            `  - ${code(param)} = ${code(key)}: ${prose(variant.replace(/\s+/g, ' '))}`,
        ),
      ].join('\n');
    })
    .join('\n');
}

// The manifest schema.

interface Schema {
  $ref?: string;
  type?: string;
  description?: string;
  enum?: unknown[];
  const?: unknown;
  default?: unknown;
  pattern?: string;
  minLength?: number;
  maxLength?: number;
  minimum?: number;
  maximum?: number;
  minItems?: number;
  maxItems?: number;
  maxProperties?: number;
  uniqueItems?: boolean;
  required?: string[];
  properties?: Record<string, Schema>;
  patternProperties?: Record<string, Schema>;
  additionalProperties?: boolean | Schema;
  items?: Schema;
  $defs?: Record<string, Schema>;
}

interface Field {
  path: string;
  schema: Schema;
  required: boolean;
  /** For a key of `patternProperties`: the pattern its name matches. */
  keyPattern?: string;
  /** For such a key with the same schema as a named property: that property's path. */
  sameAs?: string;
}

let manifestCache: Schema | undefined;
function manifestSchema(): Schema {
  manifestCache ??= JSON.parse(
    readFileSync(join(repo, 'sdk', 'schema', 'manifest.v2.schema.json'), 'utf8'),
  ) as Schema;
  return manifestCache;
}

/** A node with its `$ref` followed: the referenced definition with the node's own keys over it. */
function resolved(node: Schema): Schema {
  if (node.$ref === undefined) {
    return node;
  }
  const name = node.$ref.replace('#/$defs/', '');
  const target = manifestSchema().$defs?.[name];
  if (target === undefined) {
    throw new Error(`manifest schema: unknown $ref ${node.$ref}`);
  }
  const own: Schema = { ...node };
  delete own.$ref;
  return { ...resolved(target), ...own };
}

/** The fields below a node, each object's children after it; array items appear as `<path>[].<key>`. */
function children(schema: Schema, path: string, into: Field[]): Field[] {
  const child = (key: string) => (path === '' ? key : `${path}.${key}`);
  for (const [key, node] of Object.entries(schema.properties ?? {})) {
    field(node, child(key), schema.required?.includes(key) ?? false, into);
  }
  for (const [pattern, node] of Object.entries(schema.patternProperties ?? {})) {
    const key = pattern.startsWith('^[a-z]{2,3}') ? '<language>' : '<name>';
    const twin = Object.entries(schema.properties ?? {}).find(
      ([, named]) => JSON.stringify(resolved(named)) === JSON.stringify(resolved(node)),
    );
    if (twin === undefined) {
      field(node, child(key), false, into, pattern);
    } else {
      into.push({
        path: child(key),
        schema: resolved(node),
        required: false,
        keyPattern: pattern,
        sameAs: child(twin[0]),
      });
    }
  }
  const items = schema.items === undefined ? undefined : resolved(schema.items);
  if (items?.properties !== undefined) {
    children(items, `${path}[]`, into);
  }
  return into;
}

function field(node: Schema, path: string, required: boolean, into: Field[], keyPattern?: string) {
  const schema = resolved(node);
  if (schema.description === undefined) {
    report.fieldsWithoutDescription.add(path);
  }
  into.push({ path, schema, required, ...(keyPattern === undefined ? {} : { keyPattern }) });
  children(schema, path, into);
}

function typeName(schema: Schema, locale: Locale): string {
  if (schema.const !== undefined) {
    return typeof schema.const === 'number' ? 'integer' : typeof schema.const;
  }
  if (schema.type === 'array' && schema.items !== undefined) {
    return t(locale, 'arrayOf', { type: typeName(resolved(schema.items), locale) });
  }
  return schema.type ?? (schema.enum === undefined ? 'object' : 'string');
}

function patternText(pattern: string, locale: Locale): string {
  return pattern.length > 90 ? t(locale, 'longPattern', { n: pattern.length }) : code(pattern);
}

function constraints(schema: Schema, locale: Locale): string[] {
  const parts: string[] = [];
  if (schema.const !== undefined) {
    parts.push(code(JSON.stringify(schema.const)));
  }
  if (schema.enum !== undefined) {
    parts.push(schema.enum.map((value) => code(String(value))).join(', '));
  }
  if (schema.minLength !== undefined && schema.maxLength !== undefined) {
    parts.push(t(locale, 'chars', { min: schema.minLength, max: schema.maxLength }));
  } else if (schema.maxLength !== undefined) {
    parts.push(t(locale, 'atMostChars', { max: schema.maxLength }));
  } else if (schema.minLength !== undefined) {
    parts.push(t(locale, 'atLeastChars', { min: schema.minLength }));
  }
  if (schema.pattern !== undefined) {
    parts.push(patternText(schema.pattern, locale));
  }
  if (schema.minimum !== undefined && schema.maximum !== undefined) {
    parts.push(t(locale, 'range', { min: schema.minimum, max: schema.maximum }));
  }
  if (schema.minItems !== undefined && schema.maxItems !== undefined) {
    parts.push(t(locale, 'items', { min: schema.minItems, max: schema.maxItems }));
  } else if (schema.maxItems !== undefined) {
    parts.push(t(locale, 'atMostItems', { max: schema.maxItems }));
  } else if (schema.minItems !== undefined) {
    parts.push(t(locale, 'atLeastItems', { min: schema.minItems }));
  }
  if (schema.maxProperties !== undefined) {
    parts.push(t(locale, 'atMostProperties', { max: schema.maxProperties }));
  }
  if (schema.uniqueItems === true) {
    parts.push(t(locale, 'unique'));
  }
  if (schema.type === 'array' && schema.items !== undefined) {
    const items = resolved(schema.items);
    if (items.type !== 'object' && items.properties === undefined) {
      parts.push(...constraints(items, locale));
    }
  }
  return parts;
}

/** For an array of plain values: the description of its entries, which have no row of their own. */
function itemDescription(schema: Schema, locale: Locale): string {
  const items = schema.items === undefined ? undefined : resolved(schema.items);
  return items?.description === undefined || items.properties !== undefined
    ? ''
    : `${t(locale, 'eachEntry')} ${items.description}`;
}

function fieldRow(field: Field, locale: Locale): string[] {
  const description = [
    field.keyPattern === undefined ? '' : `${t(locale, 'keyName')} ${code(field.keyPattern)}.`,
    field.sameAs === undefined ? '' : t(locale, 'sameAs', { field: code(field.sameAs) }),
    field.sameAs === undefined ? (field.schema.description ?? '') : '',
    itemDescription(field.schema, locale),
  ];
  const { schema } = field;
  return [
    code(field.path),
    typeName(schema, locale),
    field.required ? t(locale, 'yes') : t(locale, 'no'),
    schema.default === undefined ? t(locale, 'none') : code(JSON.stringify(schema.default)),
    field.sameAs === undefined
      ? constraints(schema, locale).join('; ') || t(locale, 'none')
      : t(locale, 'none'),
    prose(description.filter((part) => part !== '').join(' ')),
  ];
}

function manifestFields(locale: Locale, group: string): string {
  const all = children(manifestSchema(), '', []);
  const header = [
    t(locale, 'field'),
    t(locale, 'type'),
    t(locale, 'requiredColumn'),
    t(locale, 'default'),
    t(locale, 'allowed'),
    t(locale, 'description'),
  ];
  const rows =
    group === 'top'
      ? all.filter((field) => !field.path.includes('.') && !field.path.includes('['))
      : all.filter(
          (field) => field.path.startsWith(`${group}.`) || field.path.startsWith(`${group}[]`),
        );
  if (rows.length === 0) {
    throw new Error(`manifest schema: no fields under ${group}`);
  }
  return table(
    header,
    rows.map((field) => fieldRow(field, locale)),
  );
}

// The Compose allowlist.

interface KeyRule {
  type: string;
  description?: string;
  required?: boolean;
  pattern?: string;
  values?: string[];
  min?: number | string;
  max?: number | string;
  right?: string;
  keys?: Record<string, KeyRule>;
}

interface ComposeRules {
  limits: Record<string, number>;
  patterns: Record<string, string>;
  top: Record<string, KeyRule>;
  service: Record<string, KeyRule>;
  mount: {
    keys: Record<string, KeyRule>;
    reservedTargets: string[];
    dockerSockets: string[];
    dockerSocketRight: string;
    dockerSocketRawRight: string;
  };
  dependsOn: { keys: Record<string, KeyRule> };
  dev: { service: Record<string, KeyRule>; build: { keys: Record<string, KeyRule> } };
  reservedLabelPrefixes: string[];
  elevated: {
    blockedCapabilities: string[];
    hostNamespaceRights: string[];
    namespacedDevices: string[];
    deviceClasses: Record<string, { nodes?: string; links?: string } | string>;
    tiers: { default: string; rights: Record<string, string> };
  };
  refused: Record<string, Record<string, string>>;
  descriptions?: {
    types?: Record<string, string>;
    limits?: Record<string, string>;
    patterns?: Record<string, string>;
  };
}

let rulesCache: ComposeRules | undefined;
function composeRules(): ComposeRules {
  rulesCache ??= JSON.parse(
    readFileSync(join(repo, 'sdk', 'schema', 'compose.v1.rules.json'), 'utf8'),
  ) as ComposeRules;
  return rulesCache;
}

function bytes(value: number): string {
  for (const [unit, size] of [
    ['GiB', 1024 ** 3],
    ['MiB', 1024 ** 2],
    ['KiB', 1024],
  ] as const) {
    if (value >= size && value % size === 0) {
      return `${String(value)} (${String(value / size)} ${unit})`;
    }
  }
  return String(value);
}

function limitValue(name: string): string {
  const value = composeRules().limits[name];
  if (value === undefined) {
    return name;
  }
  return name.endsWith('Bytes') ? bytes(value) : String(value);
}

function bound(value: number | string): string {
  return typeof value === 'number' || !(value in composeRules().limits)
    ? String(value)
    : limitValue(value);
}

function ruleLimits(rule: KeyRule, locale: Locale): string {
  const parts: string[] = [];
  if (rule.values !== undefined) {
    parts.push(rule.values.map(code).join(', '));
  }
  if (rule.pattern !== undefined) {
    parts.push(`${t(locale, 'pattern')} ${code(rule.pattern)}`);
  }
  if (rule.min !== undefined && rule.max !== undefined) {
    parts.push(t(locale, 'range', { min: bound(rule.min), max: bound(rule.max) }));
  } else if (rule.max !== undefined) {
    parts.push(t(locale, 'atMost', { max: bound(rule.max) }));
  } else if (rule.min !== undefined) {
    parts.push(t(locale, 'atLeast', { min: bound(rule.min) }));
  }
  return parts.join('; ') || t(locale, 'none');
}

function rightText(right: string | undefined, locale: Locale): string {
  if (right === undefined) {
    return t(locale, 'none');
  }
  const shown =
    right === 'device:' ? 'device:<path>' : right === 'capability:' ? 'capability:<NAME>' : right;
  return `${code(shown)} (${t(locale, 'usesRight')})`;
}

function keyRows(
  keys: Record<string, KeyRule>,
  prefix: string,
  locale: Locale,
  area: string,
): string[][] {
  return Object.entries(keys).flatMap(([key, rule]) => {
    const path = `${prefix}${key}`;
    if (rule.description === undefined) {
      report.rulesWithoutDescription.add(`${area}: ${path}`);
    }
    const row = [
      code(path),
      code(rule.type),
      rule.required === true ? t(locale, 'yes') : t(locale, 'no'),
      ruleLimits(rule, locale),
      rightText(rule.right, locale),
      prose(rule.description ?? ''),
    ];
    return [row, ...(rule.keys === undefined ? [] : keyRows(rule.keys, `${path}.`, locale, area))];
  });
}

function keysTable(keys: Record<string, KeyRule>, locale: Locale, area: string): string {
  return table(
    [
      t(locale, 'key'),
      t(locale, 'type'),
      t(locale, 'requiredColumn'),
      t(locale, 'limits'),
      t(locale, 'right'),
      t(locale, 'description'),
    ],
    keyRows(keys, '', locale, area),
  );
}

function composePart(part: string, locale: Locale): string {
  const rules = composeRules();
  switch (part) {
    case 'top':
      return keysTable(rules.top, locale, 'top');
    case 'service':
      return keysTable(rules.service, locale, 'service');
    case 'mount':
      return keysTable(rules.mount.keys, locale, 'mount');
    case 'depends-on':
      return keysTable(rules.dependsOn.keys, locale, 'dependsOn');
    case 'dev-service':
      return keysTable(rules.dev.service, locale, 'dev.service');
    case 'dev-build':
      return keysTable(rules.dev.build.keys, locale, 'dev.build');
    case 'reserved':
      return [
        `- ${rules.mount.reservedTargets.map(code).join(', ')}`,
        `- ${rules.mount.dockerSockets.map(code).join(', ')}: ${code(rules.mount.dockerSocketRight)}, ${code(rules.mount.dockerSocketRawRight)}`,
        `- ${rules.reservedLabelPrefixes.map(code).join(', ')}`,
      ].join('\n');
    case 'elevated':
      return [
        `- ${rules.elevated.blockedCapabilities.map(code).join(', ')}`,
        `- ${rules.elevated.hostNamespaceRights.map(code).join(', ')}`,
        `- ${rules.elevated.namespacedDevices.map(code).join(', ')}`,
        `- ${Object.keys(rules.elevated.deviceClasses)
          .filter((name) => !name.startsWith('$'))
          .map(code)
          .join(', ')}`,
        `- ${['elevated', 'high', 'critical']
          .map((tier) => {
            const rights = Object.entries(rules.elevated.tiers.rights)
              .filter(([, of]) => of === tier)
              .map(([right]) => code(right));
            if (tier === rules.elevated.tiers.default) {
              rights.push(code('*'));
            }
            return `${code(tier)}: ${rights.join(', ')}`;
          })
          .join('; ')}`,
      ].join('\n');
    case 'types':
      return table(
        [t(locale, 'type'), t(locale, 'description')],
        Object.entries(rules.descriptions?.types ?? {}).map(([name, text]) => [
          code(name),
          prose(text),
        ]),
      );
    case 'limits':
      return table(
        [t(locale, 'name'), t(locale, 'value'), t(locale, 'description')],
        Object.keys(rules.limits).map((name) => {
          const text = rules.descriptions?.limits?.[name];
          if (text === undefined) {
            report.rulesWithoutDescription.add(`limits: ${name}`);
          }
          return [code(name), limitValue(name), prose(text ?? '')];
        }),
      );
    case 'patterns':
      return table(
        [t(locale, 'name'), t(locale, 'pattern'), t(locale, 'description')],
        Object.entries(rules.patterns).map(([name, pattern]) => {
          const text = rules.descriptions?.patterns?.[name];
          if (text === undefined) {
            report.rulesWithoutDescription.add(`patterns: ${name}`);
          }
          return [code(name), patternText(pattern, locale), prose(text ?? '')];
        }),
      );
    default: {
      const refused = /^refused-(\w+)$/.exec(part);
      const keys = refused === null ? undefined : rules.refused[refused[1] ?? ''];
      if (keys === undefined) {
        throw new Error(`compose rules: unknown part ${part}`);
      }
      return table(
        [t(locale, 'key'), t(locale, 'reason')],
        Object.entries(keys).map(([key, reason]) => [code(key), prose(reason)]),
      );
    }
  }
}

// The pages.

function generate(kind: string, argument: string, locale: Locale): string {
  switch (kind) {
    case 'cli-synopsis':
      return `\`\`\`sh\n${synopsis(argument)}\n\`\`\``;
    case 'cli-usage':
      return `\`\`\`sh\npithyx ${cli().usage.replace(/^pithyx /, '')}\n\`\`\``;
    case 'cli-options':
      return optionsTable(argument, locale);
    case 'cli-global-options':
      return globalOptionsTable(locale);
    case 'cli-commands':
      return commandsTable(locale);
    case 'cli-findings':
      return findingsList(argument, locale);
    case 'manifest-fields':
      return manifestFields(locale, argument);
    case 'compose-rules':
      return composePart(argument, locale);
    default:
      throw new Error(`unknown generated part ${kind}`);
  }
}

// A part runs from its opening marker to the next closing one and never across another opening marker.
const MARKER =
  /(<!-- generated:([\w-]+)(?: ([^>]*?))? -->)(?:(?!<!-- generated:)[\s\S])*?(<!-- \/generated -->)/g;

function pages(): string[] {
  const roots = [join(docs, 'docs', 'reference'), join(docs, 'de', 'docs', 'reference')];
  const walk = (folder: string): string[] =>
    readdirSync(folder).flatMap((name) => {
      const path = join(folder, name);
      return statSync(path).isDirectory() ? walk(path) : name.endsWith('.md') ? [path] : [];
    });
  return roots.flatMap(walk);
}

const check = process.argv.includes('--check');
const stale: string[] = [];
let parts = 0;
for (const path of pages()) {
  const text = readFileSync(path, 'utf8');
  const locale: Locale = relative(docs, path).startsWith('de/') ? 'de' : 'en';
  const filled = text.replace(
    MARKER,
    (_, open: string, kind: string, argument: string | undefined, close: string) => {
      parts++;
      return `${open}\n\n${generate(kind, (argument ?? '').trim(), locale)}\n\n${close}`;
    },
  );
  // The repository's format check runs Prettier on Markdown, so the pages are written as Prettier writes them.
  const next = await format(filled, { ...(await resolveConfig(path)), filepath: path });
  if (next !== text) {
    stale.push(relative(repo, path));
    if (!check) {
      writeFileSync(path, next);
    }
  }
}

const notes = Object.entries(report).filter(([, entries]) => entries.size > 0);
for (const [name, entries] of notes) {
  console.warn(`reference: ${name}: ${[...entries].sort().join(', ')}`);
}
if (check && stale.length > 0) {
  console.error(
    `reference: stale generated parts in\n${stale.join('\n')}\nRun node scripts/generate-cli-reference.ts.`,
  );
  process.exit(1);
}
console.log(
  `reference: ${String(parts)} generated parts, ${check ? `${String(stale.length)} stale` : `${String(stale.length)} files rewritten`}`,
);
