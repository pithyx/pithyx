import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { languageName, languages, setLanguage, storedLanguage, type Language } from '../i18n';
import { Menu, MenuItem } from '@pithyx/ui';

/** Automatic plus every language, with the current choice checked; the account menu's language submenu. */
export function LanguageOptions() {
  const { t } = useTranslation();
  const [choice, setChoice] = useState<Language | 'auto'>(storedLanguage() ?? 'auto');
  return (
    <Menu
      aria-label={t('menu.language')}
      selectionMode="single"
      selectedKeys={[choice]}
      onAction={(key) => {
        const language = languages.find((code) => code === key) ?? null;
        setChoice(language ?? 'auto');
        void setLanguage(language);
      }}
    >
      <MenuItem id="auto">{t('menu.languageAuto')}</MenuItem>
      {languages.map((code) => (
        <MenuItem key={code} id={code} lang={code}>
          {languageName(code)}
        </MenuItem>
      ))}
    </Menu>
  );
}
