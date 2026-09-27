import { IconPlugConnectedX, IconRefresh } from '../ui/icons';
import { useTranslation } from 'react-i18next';
import { AuthLayout } from '../shell/AuthLayout';
import styles from './Auth.module.css';
import { Button } from './controls';

/** Shown instead of setup or sign-in when the daemon does not answer. */
export function Unreachable({ retrying, onRetry }: { retrying: boolean; onRetry: () => void }) {
  const { t } = useTranslation();
  return (
    <AuthLayout>
      <div className={styles.form} role="alert">
        <IconPlugConnectedX size={32} stroke={1.5} aria-hidden className={styles.keyIcon} />
        <div className={styles.heading}>
          <h1 className={styles.title}>{t('status.unreachable')}</h1>
          <p className={styles.intro}>{t('status.unreachableHint')}</p>
        </div>
        <Button variant="primary" className={styles.submit} pending={retrying} onClick={onRetry}>
          <IconRefresh size={18} stroke={1.75} aria-hidden />
          {t('status.retry')}
        </Button>
      </div>
    </AuthLayout>
  );
}
