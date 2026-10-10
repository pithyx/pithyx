import type { APIRoute, GetStaticPaths } from 'astro';
import { getCollection } from 'astro:content';
import { type DocEntry, isDocsPage, markdownHeaders, pageMarkdown, siteOf } from '../lib/docs.ts';

// The Markdown twin of every docs page, in both languages: /docs/admin/storage.md, /de/docs/admin/storage.md.
export const getStaticPaths = (async () => {
  const entries = await getCollection('docs', (entry) => isDocsPage(entry.id));
  return entries.map((entry) => ({ params: { slug: entry.id }, props: { entry } }));
}) satisfies GetStaticPaths;

export const GET: APIRoute<{ entry: DocEntry }> = async ({ props, site }) =>
  new Response(await pageMarkdown(props.entry, siteOf(site)), { headers: markdownHeaders });
