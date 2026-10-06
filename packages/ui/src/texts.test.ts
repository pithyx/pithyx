import { describe, expect, it } from 'vitest';
import { uiTexts } from './texts';

describe('uiTexts', () => {
  it('picks the texts by the language of the locale', () => {
    expect(uiTexts('de-AT').search.clear).toBe(uiTexts('de').search.clear);
    expect(uiTexts('de').search.clear).not.toBe(uiTexts('en').search.clear);
  });

  it('falls back to English for a language without texts', () => {
    expect(uiTexts('fr-FR')).toEqual(uiTexts('en-US'));
  });
});
