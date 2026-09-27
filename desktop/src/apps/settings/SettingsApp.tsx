import { useTranslation } from 'react-i18next';
import type { SdkAppProps } from '../sdk/CoreSdkApp';
import styles from './Settings.module.css';
import { StoragePage } from './StoragePage';

// Pages ship with the step that makes them work; Users, Shares, Network and Energy follow in M3.
const pages = ['storage'] as const;

/** Settings: a list of pages and the open one. */
export function SettingsApp({ app }: SdkAppProps) {
  const { t } = useTranslation('settings');
  return (
    <div className={styles.app}>
      <nav className={styles.nav} aria-label={t('sections')}>
        {pages.map((page) => (
          <span key={page} className={styles.navItem} aria-current="page">
            {t(`nav.${page}`)}
          </span>
        ))}
      </nav>
      <main className={styles.page}>
        <StoragePage app={app} />
      </main>
    </div>
  );
}
