import { defineRouteMiddleware } from '@astrojs/starlight/route-data';
import { isDocsPage, localeOf, markdownUrl } from './docs.ts';
import { t } from './i18n.ts';

// Every docs page points to its Markdown twin, and a page that is not checked against the code says so in a banner.
export const onRequest = defineRouteMiddleware((context) => {
  const route = context.locals.starlightRoute;
  const { entry } = route;
  if (!isDocsPage(entry.id) || entry.id.endsWith('404')) {
    return;
  }
  route.head.push({
    tag: 'link',
    attrs: {
      rel: 'alternate',
      type: 'text/markdown',
      href: markdownUrl(entry.id),
      title: 'Markdown',
    },
  });
  if (entry.data.status !== 'ready' && !entry.data.banner) {
    entry.data.banner = { content: t(localeOf(entry.id), `status.${entry.data.status}`) };
  }
});
