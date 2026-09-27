import { useTranslation } from 'react-i18next';
import styles from './Auth.module.css';

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
