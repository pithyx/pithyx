import { getCollection, type CollectionEntry } from 'astro:content';
import { errorCodesMarkdown } from './errorCodes.ts';
import { faqCategories } from './faqCategories.ts';
import { t, type Key, type Locale } from './i18n.ts';
import { allSections, type Section } from './sections.ts';

export type { Locale };
export type DocEntry = CollectionEntry<'docs'>;
export type FaqEntry = CollectionEntry<'faq'>;

export function sectionLabel(locale: Locale, section: Section): string {
  return t(locale, `section.${section.id}.label` as Key);
}

export function sectionSummary(locale: Locale, section: Section): string {
  return t(locale, `section.${section.id}.summary` as Key);
}

export function localeOf(id: string): Locale {
  return id === 'de' || id.startsWith('de/') ? 'de' : 'en';
}

/** The entry id without its locale folder: "de/docs/admin/storage" becomes "docs/admin/storage". */
export function baseId(id: string): string {
  return localeOf(id) === 'de' ? id.replace(/^de\/?/, '') : id;
}

export function localePrefix(locale: Locale): string {
  return locale === 'en' ? '' : `/${locale}`;
}

export function pageUrl(id: string): string {
  return `/${id}/`;
}

/** The Markdown twin of a page: /docs/admin/storage/ is also /docs/admin/storage.md. */
export function markdownUrl(id: string): string {
  return `/${id}.md`;
}

export function isDocsPage(id: string): boolean {
  const base = baseId(id);
  return base === 'docs' || base.startsWith('docs/');
}

/** The deepest section whose folder holds the page, or undefined for the docs index and the FAQ. */
export function sectionOf(id: string): Section | undefined {
  const path = baseId(id).replace(/^docs\/?/, '');
  return allSections
    .filter((section) => path.startsWith(`${section.directory}/`))
    .sort((a, b) => b.directory.length - a.directory.length)[0];
}

function sectionRank(id: string): number {
  const base = baseId(id);
  if (base === 'docs') {
    return -1;
  }
  const section = sectionOf(id);
  return section ? allSections.indexOf(section) : allSections.length;
}

function sidebarOrder(entry: DocEntry): number {
  return entry.data.sidebar.order ?? Number.MAX_SAFE_INTEGER;
}

/** Docs pages of one locale in sidebar order: the index, the sections in order, then the FAQ. */
export async function docsInOrder(locale: Locale, section?: Section): Promise<DocEntry[]> {
  const entries = await getCollection(
    'docs',
    (entry) => localeOf(entry.id) === locale && isDocsPage(entry.id),
  );
  const wanted = section ? [section, ...(section.children ?? [])] : undefined;
  return entries
    .filter((entry) => {
      if (!wanted) {
        return true;
      }
      const own = sectionOf(entry.id);
      return own !== undefined && wanted.includes(own);
    })
    .sort(
      (a, b) =>
        sectionRank(a.id) - sectionRank(b.id) ||
        sidebarOrder(a) - sidebarOrder(b) ||
        a.data.title.localeCompare(b.data.title),
    );
}

export async function faqInOrder(locale: Locale): Promise<FaqEntry[]> {
  const entries = await getCollection('faq', (entry) => entry.id.startsWith(`${locale}/`));
  const rank = (entry: FaqEntry) => faqCategories.indexOf(entry.data.category);
  return entries.sort(
    (a, b) =>
      rank(a) - rank(b) ||
      a.data.order - b.data.order ||
      a.data.question.localeCompare(b.data.question),
  );
}

export function faqMarkdown(entries: FaqEntry[]): string {
  return entries
    .map((entry) => `## ${entry.data.question}\n\n${(entry.body ?? '').trim()}\n`)
    .join('\n');
}

/** The site's address from astro.config.ts, which every endpoint needs for absolute links. */
export function siteOf(site: URL | undefined): URL {
  if (!site) {
    throw new Error('site is not set in astro.config.ts');
  }
  return site;
}

function absolute(site: URL, path: string): string {
  return new URL(path, site).href;
}

/**
 * MDX bodies turned into plain Markdown for the twins and bundles: imports go, Starlight components become lists and
 * bold labels, asides become quotes, and site links become absolute. Authors write plain Markdown where they can;
 * scripts/check-build.ts fails when a component survives.
 */
export function toPlainMarkdown(body: string, site: URL): string {
  const lines: string[] = [];
  let inAside = false;
  for (const line of body.split('\n')) {
    if (/^(import|export)\s.+$/.test(line)) {
      continue;
    }
    const asideStart = /^:::(note|tip|caution|danger)(?:\[(.*)\])?\s*$/.exec(line);
    if (asideStart) {
      inAside = true;
      const kind = asideStart[1] ?? '';
      const label = asideStart[2] ?? kind.charAt(0).toUpperCase() + kind.slice(1);
      lines.push(`> **${label}:**`, '>');
      continue;
    }
    if (inAside && /^:::\s*$/.test(line)) {
      inAside = false;
      continue;
    }
    if (inAside) {
      lines.push(line.trim() === '' ? '>' : `> ${line}`);
      continue;
    }
    const linkCard = /^\s*<LinkCard\s+(.*?)\/>\s*$/.exec(line);
    if (linkCard) {
      const attributes = linkCard[1] ?? '';
      const attr = (name: string) => new RegExp(`${name}="([^"]*)"`).exec(attributes)?.[1];
      const description = attr('description');
      lines.push(
        `- [${attr('title') ?? ''}](${attr('href') ?? ''})${description ? `: ${description}` : ''}`,
      );
      continue;
    }
    const titled = /^\s*<(Card|TabItem)\s+(?:title|label)="([^"]*)"[^>]*>\s*$/.exec(line);
    if (titled) {
      lines.push(`**${titled[2] ?? ''}**`, '');
      continue;
    }
    if (/^\s*<\/?(CardGrid|Card|Steps|Tabs|TabItem)\b[^>]*>\s*$/.test(line)) {
      continue;
    }
    // A component whose output the page appends itself (frontmatter `generated`).
    if (/^\s*<[A-Z]\w*\b[^>]*\/>\s*$/.test(line)) {
      continue;
    }
    lines.push(line);
  }
  return lines
    .join('\n')
    .replace(/\]\((\/[^)\s]*)\)/g, (_, path: string) => `](${absolute(site, path)})`)
    .replace(/\n{3,}/g, '\n\n')
    .trim();
}

async function overviewMarkdown(locale: Locale, site: URL): Promise<string> {
  const lines = await Promise.all(
    allSections
      .filter((section) => !section.children)
      .map(async (section) => {
        const first = (await docsInOrder(locale, section))[0];
        const href = first ? markdownUrl(first.id) : bundleUrl(locale, section);
        return `- [${sectionLabel(locale, section)}](${absolute(site, href)}): ${sectionSummary(locale, section)}`;
      }),
  );
  return lines.join('\n');
}

async function generatedMarkdown(entry: DocEntry, site: URL): Promise<string> {
  const locale = localeOf(entry.id);
  switch (entry.data.generated) {
    case 'overview':
      return overviewMarkdown(locale, site);
    case 'faq':
      return faqMarkdown(await faqInOrder(locale));
    case 'error-codes':
      return errorCodesMarkdown(locale);
    case undefined:
      return '';
  }
}

/** One page as Markdown: title, summary, where it lives, its status when not checked yet, then the body. */
export async function pageMarkdown(
  entry: DocEntry,
  site: URL,
  headingLevel: 1 | 2 = 1,
): Promise<string> {
  const hashes = '#'.repeat(headingLevel);
  const meta = [
    `Source: ${absolute(site, pageUrl(entry.id))}`,
    `Markdown: ${absolute(site, markdownUrl(entry.id))}`,
    ...(entry.data.status === 'ready' ? [] : [`Status: ${entry.data.status}`]),
  ];
  let body = toPlainMarkdown(entry.body ?? '', site);
  if (headingLevel === 2) {
    body = body.replace(/^(#{1,5}) /gm, '#$1 ');
  }
  const generated = await generatedMarkdown(entry, site);
  return [
    `${hashes} ${entry.data.title}`,
    `> ${entry.data.description}`,
    meta.join('\n'),
    body,
    headingLevel === 2 ? generated.replace(/^(#{1,5}) /gm, '#$1 ') : generated,
  ]
    .filter((part) => part.trim() !== '')
    .join('\n\n')
    .concat('\n');
}

export const textHeaders = { 'Content-Type': 'text/plain; charset=utf-8' };
export const markdownHeaders = { 'Content-Type': 'text/markdown; charset=utf-8' };

export function bundleUrl(locale: Locale, section?: Section): string {
  return section
    ? `${localePrefix(locale)}/llms/${section.id}.txt`
    : `${localePrefix(locale)}/llms-full.txt`;
}

/** /llms.txt in the llmstxt.org format: a title, a summary, then sections of links with one line each. */
export async function llmsIndex(locale: Locale, site: URL): Promise<string> {
  const entries = await docsInOrder(locale);
  const link = (title: string, path: string, description: string) =>
    `- [${title}](${absolute(site, path)}): ${description}`;

  const out = [
    `# Pithyx`,
    `> ${t(locale, 'llms.summary')}`,
    t(locale, 'llms.usage', { full: absolute(site, bundleUrl(locale)) }),
    `## ${t(locale, 'llms.bundles')}`,
  ];
  out.push(
    [
      link('llms-full.txt', bundleUrl(locale), t(locale, 'llms.everything')),
      ...allSections.map((section) =>
        link(
          sectionLabel(locale, section),
          bundleUrl(locale, section),
          sectionSummary(locale, section),
        ),
      ),
      link('FAQ', `${localePrefix(locale)}/llms/faq.txt`, t(locale, 'llms.faq')),
    ].join('\n'),
  );

  const grouped = new Map<string, DocEntry[]>();
  for (const entry of entries) {
    const key = sectionOf(entry.id)?.id ?? '';
    grouped.set(key, [...(grouped.get(key) ?? []), entry]);
  }
  const groups: [string, DocEntry[]][] = [
    ['Pithyx', grouped.get('') ?? []],
    ...allSections.map((section): [string, DocEntry[]] => [
      section.children ? '' : sectionLabel(locale, section),
      grouped.get(section.id) ?? [],
    ]),
  ];
  for (const [title, pages] of groups) {
    if (title === '' || pages.length === 0) {
      continue;
    }
    out.push(
      `## ${title}`,
      pages
        .map((page) => link(page.data.title, markdownUrl(page.id), page.data.description))
        .join('\n'),
    );
  }

  const other: Locale = locale === 'en' ? 'de' : 'en';
  out.push(
    `## ${t(locale, 'llms.optional')}`,
    link(
      t(locale, 'llms.otherLanguageName'),
      `${localePrefix(other)}/llms.txt`,
      t(locale, 'llms.otherLanguage'),
    ),
  );
  return out.join('\n\n').concat('\n');
}

/** A bundle: every page of one section (or of all of them, plus the FAQ entries) as one Markdown text. */
export async function llmsBundle(locale: Locale, site: URL, section?: Section): Promise<string> {
  const entries = await docsInOrder(locale, section);
  const title = section ? `Pithyx: ${sectionLabel(locale, section)}` : 'Pithyx';
  const summary = section ? sectionSummary(locale, section) : t(locale, 'llms.summary');
  const pages = await Promise.all(entries.map((entry) => pageMarkdown(entry, site, 2)));
  return [`# ${title}`, `> ${summary}`, ...pages].join('\n\n');
}

export async function faqBundle(locale: Locale, site: URL): Promise<string> {
  const entries = await getCollection(
    'docs',
    (entry) => baseId(entry.id) === 'docs/faq' && localeOf(entry.id) === locale,
  );
  const page = entries[0];
  if (!page) {
    throw new Error(`docs/faq is missing for ${locale}`);
  }
  return pageMarkdown(page, site);
}
