import { useTranslation } from 'react-i18next';
import styles from './Settings.module.css';

/** File services: SMB and SFTP arrive with v0.1 (step 12); until then the page says so plainly. */
export function FileServicesPage() {
  const { t } = useTranslation('settings');
  return (
    <div className={styles.content}>
      <header className={styles.pageHeader}>
        <div>
          <h2 className={styles.pageTitle}>{t('pages.fileServices.title')}</h2>
          <p className={styles.muted}>{t('fileServices.intro')}</p>
        </div>
      </header>
      <p className={styles.notice} role="note">
        <span className={styles.badge}>{t('fileServices.coming')}</span> {t('fileServices.explain')}
      </p>
      <ul className={styles.plainList}>
        <li>{t('fileServices.smb')}</li>
        <li>{t('fileServices.sftp')}</li>
      </ul>
    </div>
  );
}
