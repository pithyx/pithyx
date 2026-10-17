// Writes the REST API reference under reference/api/ (gitignored) from packages/api-client/openapi.json, the document
// the daemon generates from its code: one page per tag with every operation, and one page with every schema, in English
// and German (translated headings; descriptions stay English as the daemon writes them).
import { mkdirSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';

type Lang = 'en' | 'de';

interface Schema {
  $ref?: string;
  type?: string | string[];
  format?: string;
  enum?: unknown[];
  items?: Schema;
  oneOf?: Schema[];
  anyOf?: Schema[];
  allOf?: Schema[];
  properties?: Record<string, Schema>;
  additionalProperties?: Schema | boolean;
  required?: string[];
  description?: string;
  default?: unknown;
}

interface Parameter {
  name: string;
  in: string;
  required?: boolean;
  description?: string;
  schema?: Schema;
}

interface Content {
  content?: Record<string, { schema?: Schema }>;
  description?: string;
  required?: boolean;
}

interface Operation {
  tags?: string[];
  summary?: string;
  description?: string;
  operationId?: string;
  parameters?: Parameter[];
  requestBody?: Content;
  responses?: Record<string, Content>;
}

interface Document {
  info: { title: string; version: string };
  tags?: { name: string; description?: string }[];
  paths: Record<string, Record<string, Operation>>;
  components: { schemas: Record<string, Schema> };
}

const website = join(import.meta.dirname, '..');
const repo = join(website, '..');
const roots: Record<Lang, string> = {
  en: join(website, 'src/content/docs/docs/reference/api'),
  de: join(website, 'src/content/docs/de/docs/reference/api'),
};
const methods = ['get', 'post', 'put', 'patch', 'delete'] as const;
const strings: Record<Lang, Record<string, string>> = {
  en: JSON.parse(readFileSync(join(website, 'src/content/i18n/en.json'), 'utf8')) as Record<
    string,
    string
  >,
  de: JSON.parse(readFileSync(join(website, 'src/content/i18n/de.json'), 'utf8')) as Record<
    string,
    string
  >,
};

function text(lang: Lang, key: string, params: Record<string, string | number> = {}): string {
  const value = strings[lang][`reference.api.${key}`];
  if (value === undefined) {
    throw new Error(`src/content/i18n/${lang}.json has no reference.api.${key}`);
  }
  return value.replace(/\{(\w+)\}/g, (_, name: string) => String(params[name] ?? `{${name}}`));
}

function prefix(lang: Lang): string {
  return lang === 'en' ? '' : '/de';
}

/** Descriptions come from the daemon's XML docs; internal decision numbers and line breaks stay out of the tables. */
function clean(value: string | undefined): string {
  return (value ?? '')
    .replace(/\s*\(D\d{3}(?:,\s*D\d{3})*\)/g, '')
    .replace(/\s+/g, ' ')
    .trim();
}

function cell(value: string): string {
  return value.replace(/\|/g, '\\|');
}

function slug(name: string): string {
  return name
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-|-$/g, '');
}

function schemaLink(lang: Lang, name: string): string {
  return `[${name}](${prefix(lang)}/docs/reference/api/schemas/#${slug(name)})`;
}

/** A schema as a short type: a link for a named schema, `T[]`, `A \| null`, the enum's values. */
function typeOf(lang: Lang, schema: Schema | undefined): string {
  if (schema === undefined) {
    return '';
  }
  if (schema.$ref !== undefined) {
    return schemaLink(lang, schema.$ref.split('/').at(-1) ?? '');
  }
  const variants = schema.oneOf ?? schema.anyOf ?? schema.allOf;
  if (variants !== undefined) {
    return variants.map((variant) => typeOf(lang, variant)).join(' \\| ');
  }
  if (schema.enum !== undefined) {
    return schema.enum.map((value) => `\`${JSON.stringify(value)}\``).join(', ');
  }
  const types = Array.isArray(schema.type) ? schema.type : [schema.type ?? 'object'];
  return types
    .map((type) => {
      if (type === 'array') {
        return `${typeOf(lang, schema.items)}[]`;
      }
      if (type === 'object' && typeof schema.additionalProperties === 'object') {
        return `map of ${typeOf(lang, schema.additionalProperties)}`;
      }
      return schema.format === undefined ? type : `${type} (${schema.format})`;
    })
    .join(' \\| ');
}

function describe(schema: Schema | undefined): string {
  if (schema === undefined) {
    return '';
  }
  const own =
    schema.description ?? schema.oneOf?.find((variant) => variant.description)?.description;
  return clean(own);
}

function bodyLines(lang: Lang, content: Content | undefined): string[] {
  return Object.entries(content?.content ?? {}).map(
    ([type, entry]) =>
      `\`${type}\`${entry.schema === undefined ? '' : `: ${typeOf(lang, entry.schema)}`}`,
  );
}

function operationSection(lang: Lang, path: string, method: string, operation: Operation): string {
  const lines = [`## ${method.toUpperCase()} ${path}`, ''];
  const description = clean(operation.description ?? operation.summary);
  if (description !== '') {
    lines.push(description, '');
  }
  const parameters = operation.parameters ?? [];
  if (parameters.length > 0) {
    lines.push(
      `**${text(lang, 'parameters')}**`,
      '',
      `| ${text(lang, 'name')} | ${text(lang, 'in')} | ${text(lang, 'type')} | ${text(lang, 'required')} | ${text(lang, 'description')} |`,
      '| --- | --- | --- | --- | --- |',
      ...parameters.map(
        (parameter) =>
          `| \`${parameter.name}\` | ${parameter.in} | ${typeOf(lang, parameter.schema)} | ${parameter.required === true ? text(lang, 'yes') : text(lang, 'no')} | ${cell(clean(parameter.description ?? parameter.schema?.description))} |`,
      ),
      '',
    );
  }
  const body = bodyLines(lang, operation.requestBody);
  if (body.length > 0) {
    lines.push(`**${text(lang, 'body')}**: ${body.join(', ')}`, '');
  }
  const responses = Object.entries(operation.responses ?? {}).filter(
    ([status]) => status !== 'default',
  );
  if (responses.length > 0) {
    lines.push(`**${text(lang, 'responses')}**`, '');
    for (const [status, response] of responses) {
      const shapes = bodyLines(lang, response);
      lines.push(`- ${status}${shapes.length > 0 ? `: ${shapes.join(', ')}` : ''}`);
    }
    lines.push('');
  }
  lines.push(text(lang, 'problem', { link: `${prefix(lang)}/docs/reference/errors/` }), '');
  return lines.join('\n');
}

function schemaSection(lang: Lang, name: string, schema: Schema): string {
  const lines = [`## ${name}`, ''];
  const description = describe(schema);
  if (description !== '') {
    lines.push(description, '');
  }
  const properties = Object.entries(schema.properties ?? {});
  if (properties.length > 0) {
    const required = new Set(schema.required ?? []);
    lines.push(
      `| ${text(lang, 'field')} | ${text(lang, 'type')} | ${text(lang, 'required')} | ${text(lang, 'description')} |`,
      '| --- | --- | --- | --- |',
      ...properties.map(
        ([property, value]) =>
          `| \`${property}\` | ${typeOf(lang, value)} | ${required.has(property) ? text(lang, 'yes') : text(lang, 'no')} | ${cell(describe(value))} |`,
      ),
      '',
    );
  } else {
    lines.push(`${text(lang, 'type')}: ${typeOf(lang, schema)}`, '');
  }
  return lines.join('\n');
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

function write(path: string, content: string) {
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, `${content.trim()}\n`);
}

function cleanFolder(folder: string) {
  mkdirSync(folder, { recursive: true });
  for (const name of readdirSync(folder)) {
    if (name !== '.gitignore') {
      rmSync(join(folder, name), { recursive: true, force: true });
    }
  }
}

function main() {
  const document = JSON.parse(
    readFileSync(join(repo, 'packages/api-client/openapi.json'), 'utf8'),
  ) as Document;
  const byTag = new Map<string, [string, string, Operation][]>();
  for (const [path, item] of Object.entries(document.paths)) {
    for (const method of methods) {
      const operation = item[method];
      if (operation === undefined) {
        continue;
      }
      const tag = operation.tags?.[0] ?? 'Other';
      byTag.set(tag, [...(byTag.get(tag) ?? []), [path, method, operation]]);
    }
  }
  const tags = [...byTag.keys()].sort((a, b) => a.localeCompare(b));
  const schemas = Object.entries(document.components.schemas).sort(([a], [b]) =>
    a.localeCompare(b),
  );
  let count = 0;
  for (const lang of ['en', 'de'] as const) {
    cleanFolder(roots[lang]);
    const operations = [...byTag.values()].reduce((sum, list) => sum + list.length, 0);
    write(
      join(roots[lang], 'index.md'),
      frontmatter(text(lang, 'index.title'), text(lang, 'index.description'), 1) +
        [
          text(lang, 'index.intro', { operations, version: document.info.version }),
          tags
            .map(
              (tag) =>
                `- [${tag}](${prefix(lang)}/docs/reference/api/${slug(tag)}/): ${text(lang, 'count', { count: byTag.get(tag)?.length ?? 0 })}`,
            )
            .join('\n'),
          `- [${text(lang, 'schemas.title')}](${prefix(lang)}/docs/reference/api/schemas/): ${text(lang, 'schemas.count', { count: schemas.length })}`,
        ].join('\n\n'),
    );
    tags.forEach((tag, index) => {
      const list = (byTag.get(tag) ?? []).sort(([a], [b]) => a.localeCompare(b));
      write(
        join(roots[lang], `${slug(tag)}.md`),
        frontmatter(
          text(lang, 'tag.title', { tag }),
          text(lang, 'tag.description', { tag, count: list.length }),
          2 + index,
        ) +
          [
            text(lang, 'tag.intro', { link: `${prefix(lang)}/docs/developers/daemon/api/` }),
            ...list.map(([path, method, operation]) =>
              operationSection(lang, path, method, operation),
            ),
          ].join('\n\n'),
      );
      count++;
    });
    write(
      join(roots[lang], 'schemas.md'),
      frontmatter(text(lang, 'schemas.title'), text(lang, 'schemas.description'), 2 + tags.length) +
        [
          text(lang, 'schemas.intro'),
          ...schemas.map(([name, schema]) => schemaSection(lang, name, schema)),
        ].join('\n\n'),
    );
    count += 2;
  }
  console.log(`reference: ${String(count)} pages in reference/api/ (en and de)`);
}

main();
