import { useTranslation } from 'react-i18next';
import { Button } from '../../ui/Button';
import type { SdkAppProps } from '../sdk/CoreSdkApp';
import { internalCall } from '../sdk/internal';
import styles from './Settings.module.css';

/**
 * Settings: system settings only (users and groups, shared folders, network, energy, updates). Pages ship with the
 * step that makes them work; storage has its own app.
 */
export function SettingsApp({ app }: SdkAppProps) {
  const { t } = useTranslation('settings');
  return (
    <div className={styles.app}>
      <main className={styles.page}>
        <div className={styles.pointer}>
          <h2 className={styles.pageTitle}>{t('storagePointer.title')}</h2>
          <p className={styles.muted}>{t('storagePointer.body')}</p>
          <Button
            onPress={() => {
              void internalCall(app, 'internal.admin.openApp', { app: 'storage' });
            }}
          >
            {t('storagePointer.open')}
          </Button>
        </div>
      </main>
    </div>
  );
}
