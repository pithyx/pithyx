import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { languages, setLanguage, storedLanguage, type Language } from '../i18n';

// Language names are written in their own language on purpose, so they are not translated.
const LANGUAGE_NAMES: Record<Language, string> = { en: 'English', de: 'Deutsch' };

export function LanguageSelect() {
  const { t } = useTranslation();
  const id = useId();
  const [choice, setChoice] = useState<Language | 'auto'>(storedLanguage() ?? 'auto');

  return (
    <span className="language-select">
      <label htmlFor={id} className="visually-hidden">
        {t('menu.language')}
      </label>
      <select
        id={id}
        value={choice}
        onChange={(event) => {
          const value = event.target.value as Language | 'auto';
          setChoice(value);
          void setLanguage(value === 'auto' ? null : value);
        }}
      >
        <option value="auto">{t('menu.languageAuto')}</option>
        {languages.map((language) => (
          <option key={language} value={language} lang={language}>
            {LANGUAGE_NAMES[language]}
          </option>
        ))}
      </select>
    </span>
  );
}
