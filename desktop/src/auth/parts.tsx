import { IconLanguage } from '@tabler/icons-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { languages, setLanguage, storedLanguage, type Language } from '../i18n';
import { Button } from '../ui/Button';
import { Menu, MenuItem, MenuPopover, MenuTrigger } from '../ui/Menu';
import styles from './Auth.module.css';

/** Language names are shown in their own language, so everyone finds theirs. */
function languageName(code: string): string {
  return new Intl.DisplayNames([code], { type: 'language' }).of(code) ?? code;
}

/** The language picked on this device, or 'auto' for the browser language. */
function useLanguageChoice() {
  const [choice, setChoice] = useState<Language | 'auto'>(storedLanguage() ?? 'auto');
  return {
    choice,
    choose: (key: string) => {
      const language = languages.find((code) => code === key) ?? null;
      setChoice(language ?? 'auto');
      void setLanguage(language);
    },
  };
}

/** Automatic plus every language, with the current choice checked. */
export function LanguageOptions() {
  const { t } = useTranslation();
  const { choice, choose } = useLanguageChoice();
  return (
    <Menu
      aria-label={t('menu.language')}
      selectionMode="single"
      selectedKeys={[choice]}
      onAction={(key) => {
        choose(String(key));
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

/** Language switch below the sign-in card. The choice is kept on this device. */
export function LanguageMenu() {
  const { t, i18n } = useTranslation();
  const current = languageName(i18n.language);
  return (
    <MenuTrigger>
      <Button
        variant="quiet"
        size="sm"
        aria-label={t('menu.languageCurrent', { language: current })}
      >
        <IconLanguage size={16} stroke={1.75} aria-hidden />
        {current}
      </Button>
      <MenuPopover placement="top">
        <LanguageOptions />
      </MenuPopover>
    </MenuTrigger>
  );
}

/** Two lines of groups; the key only breaks at a hyphen. */
export function RecoveryKeyValue({ value }: { value: string }) {
  const groups = value.split('-');
  const half = Math.ceil(groups.length / 2);
  return (
    <code className={styles.keyValue} data-testid="recovery-key">
      <span className={styles.keyLine}>{`${groups.slice(0, half).join('-')}-`}</span>
      <span className={styles.keyLine}>{groups.slice(half).join('-')}</span>
    </code>
  );
}

const graphemes = new Intl.Segmenter();

function firstGrapheme(word: string): string {
  for (const { segment } of graphemes.segment(word)) {
    return segment;
  }
  return '';
}

/** First letters of the first and last word of a name. */
export function initialsOf(name: string): string {
  const words = name.trim().split(/\s+/).filter(Boolean);
  const first = firstGrapheme(words.at(0) ?? '');
  const last = words.length > 1 ? firstGrapheme(words.at(-1) ?? '') : '';
  return `${first}${last}`.toLocaleUpperCase();
}

/** Shown after sign-in for exactly as long as the desktop takes to load. */
export function WelcomeCard({ name, initials }: { name: string; initials: string }) {
  const { t } = useTranslation('auth');
  return (
    <div className={styles.welcome} role="status">
      <span className={styles.bigAvatar} aria-hidden>
        {initials}
      </span>
      <h1 className={styles.welcomeTitle}>{t('login.welcome', { name })}</h1>
      <div className={styles.preparing}>
        <p>{t('login.preparing')}</p>
        <span className={styles.indeterminate} aria-hidden />
      </div>
    </div>
  );
}
