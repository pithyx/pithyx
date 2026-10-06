import { useTranslation } from 'react-i18next';
import { Button, Dialog } from '@pithyx/ui';
import { IconRestore } from '../../ui/icons';
import styles from './Settings.module.css';

/** What restore points are, in plain words with examples: what they cost, what they protect against, what not. */
export function RestorePointsDialog({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation('settings');
  return (
    <Dialog
      isOpen
      isDismissable
      onOpenChange={(open) => {
        if (!open) {
          onClose();
        }
      }}
      icon={<IconRestore size={22} stroke={1.75} aria-hidden />}
      title={t('restorePoints.title')}
      actions={
        <Button variant="primary" onPress={onClose}>
          {t('restorePoints.close')}
        </Button>
      }
    >
      <ul className={styles.explainList}>
        <li>{t('restorePoints.what')}</li>
        <li>{t('restorePoints.restore')}</li>
        <li>{t('restorePoints.space')}</li>
        <li>{t('restorePoints.deleted')}</li>
        <li>{t('restorePoints.protects')}</li>
        <li>
          <strong>{t('restorePoints.notBackup')}</strong>
        </li>
      </ul>
    </Dialog>
  );
}
