import {
  SETTINGS_PAGE_IDS,
  type SettingsGroup,
  type SettingsPageId,
} from '../apps/settings/pageIds';

export interface SettingsMatch {
  page: SettingsPageId;
  group: SettingsGroup;
  title: string;
}

/**
 * The pages of Settings a search of the Pithyx menu finds (D394: the menu's search finds apps, files and settings): by
 * the page's title, description or keywords in the user's language, titles that start with the query first.
 * `text` returns a text of the `settings` namespace.
 */
export function settingsMatches(
  query: string,
  text: (key: string) => string,
  locale: string,
): SettingsMatch[] {
  const needle = query.trim().toLocaleLowerCase(locale);
  if (needle === '') {
    return [];
  }
  const found = SETTINGS_PAGE_IDS.map(({ id, group }) => {
    const title = text(`pages.${id}.title`);
    const words = [title, text(`pages.${id}.description`), text(`pages.${id}.keywords`)]
      .join(' ')
      .toLocaleLowerCase(locale);
    return {
      page: id,
      group,
      title,
      starts: title.toLocaleLowerCase(locale).startsWith(needle),
      hit: words.includes(needle),
    };
  }).filter((entry) => entry.hit);
  return [...found.filter((entry) => entry.starts), ...found.filter((entry) => !entry.starts)].map(
    ({ page, group, title }) => ({ page, group, title }),
  );
}
