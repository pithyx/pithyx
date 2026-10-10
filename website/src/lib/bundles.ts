import { faqBundle, llmsBundle, textHeaders, type Locale } from './docs.ts';
import { allSections } from './sections.ts';

/** /llms/<id>.txt for every section and its children, plus /llms/faq.txt. */
export function sectionBundlePaths() {
  return [...allSections.map((section) => section.id), 'faq'].map((section) => ({
    params: { section },
  }));
}

export async function sectionBundleResponse(
  locale: Locale,
  id: string,
  site: URL,
): Promise<Response> {
  const section = allSections.find((candidate) => candidate.id === id);
  const text = section ? await llmsBundle(locale, site, section) : await faqBundle(locale, site);
  return new Response(text, { headers: textHeaders });
}
