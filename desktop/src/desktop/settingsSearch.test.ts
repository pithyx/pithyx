import { describe, expect, it } from 'vitest';
import { SETTINGS_PAGES } from '../apps/settings/pages';
import { SETTINGS_PAGE_IDS } from '../apps/settings/pageIds';
import { settingsMatches } from './settingsSearch';

const texts: Record<string, string> = {
  'pages.homes.title': 'Home folders',
  'pages.homes.description': 'Where the home folders live and how large they may grow',
  'pages.homes.keywords': 'quota home',
  'pages.users.title': 'Users and groups',
  'pages.users.description': 'Accounts, groups and their rights',
  'pages.users.keywords': 'account password home folder',
};
const text = (key: string) => texts[key] ?? '';

describe('settingsMatches', () => {
  it('finds a page by its title, description or keywords, titles that start with the query first', () => {
    expect(settingsMatches('home', text, 'en').map((match) => match.page)).toEqual([
      'homes',
      'users',
    ]);
    expect(settingsMatches('PASSWORD', text, 'en')).toEqual([
      { page: 'users', group: 'access', title: 'Users and groups' },
    ]);
  });

  it('finds nothing for an empty query', () => {
    expect(settingsMatches('  ', text, 'en')).toEqual([]);
  });

  it('knows every page of Settings in the order of its navigation', () => {
    expect(SETTINGS_PAGE_IDS).toEqual(SETTINGS_PAGES.map(({ id, group }) => ({ id, group })));
  });
});
