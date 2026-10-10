import react from '@astrojs/react';
import starlight from '@astrojs/starlight';
import { defineConfig } from 'astro/config';
import { t, type Key } from './src/lib/i18n.ts';
import { sections, type Section } from './src/lib/sections.ts';

function label(key: Key) {
  return { label: t('en', key), translations: { de: t('de', key) } };
}

interface SidebarGroup {
  label: string;
  translations: Record<string, string>;
  items: (SidebarGroup | { autogenerate: { directory: string } })[];
}

function sidebarGroup(section: Section): SidebarGroup {
  return {
    ...label(`section.${section.id}.label` as Key),
    items: section.children
      ? section.children.map(sidebarGroup)
      : [{ autogenerate: { directory: `docs/${section.directory}` } }],
  };
}

export default defineConfig({
  site: 'https://pithyx.org',
  trailingSlash: 'ignore',
  integrations: [
    starlight({
      title: 'Pithyx',
      description: 'Free, self-hosted NAS and server OS with a web desktop.',
      logo: { src: './src/assets/logo.svg', replacesTitle: false },
      favicon: '/favicon.svg',
      defaultLocale: 'root',
      locales: {
        root: { label: 'English', lang: 'en' },
        de: { label: 'Deutsch', lang: 'de' },
      },
      social: [{ icon: 'github', label: 'GitHub', href: 'https://github.com/pithyx/pithyx' }],
      editLink: { baseUrl: 'https://github.com/pithyx/pithyx/edit/dev/website/' },
      lastUpdated: true,
      customCss: ['@pithyx/ui/tokens.css', './src/styles/fonts.css', './src/styles/theme.css'],
      components: {
        PageTitle: './src/components/overrides/PageTitle.astro',
      },
      routeMiddleware: './src/lib/routeMiddleware.ts',
      head: [
        {
          tag: 'link',
          attrs: { rel: 'alternate', type: 'text/plain', title: 'llms.txt', href: '/llms.txt' },
        },
      ],
      sidebar: [
        { slug: 'docs', ...label('sidebar.overview') },
        ...sections.map(sidebarGroup),
        { slug: 'docs/faq' },
      ],
      credits: false,
      // The 404 page is src/content/docs/404.md, rendered by the docs route.
      disable404Route: true,
    }),
    react(),
  ],
});
