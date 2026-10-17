import i18next from 'i18next';
import { describe, expect, it } from 'vitest';
import { translateError } from './ErrorNotice';

describe('error messages', async () => {
  const i18n = i18next.createInstance();
  await i18n.init({
    lng: 'en',
    resources: { en: { errors: { 'fs.entry.not_found': 'The file was not found.' } } },
  });

  it('translates the codes it knows and ignores the English detail', () => {
    expect(
      translateError(i18n.t, {
        code: 'fs.entry.not_found',
        params: { path: 'a.txt' },
        requestId: '',
        detail: 'Entry not found',
      }),
    ).toBe('The file was not found.');
  });

  it("shows an app's own code with the app's detail", () => {
    const own = { code: 'lists.list.full' as never, params: {}, requestId: '' };
    expect(translateError(i18n.t, { ...own, detail: 'The list is full.' })).toBe(
      'The list is full.',
    );
    expect(translateError(i18n.t, own)).toBe('lists.list.full');
  });
});
