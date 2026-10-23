import { useTranslation } from 'react-i18next';
import { useInstalledApps } from '../apps/installed';
import { useAppName } from '../apps/names';
import { forgetFileDefault, useFileDefaults } from '../apps/files/fileDefaults';
import { showError } from '../errors/errorStore';
import { Button, Dialog } from '@pithyx/ui';
import styles from './Desktop.module.css';

/** The user's "Always open .x files this way" choices from Files, each with a way to forget it (D371). */
export function FileDefaultsDialog({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation();
  const choices = useFileDefaults((state) => state.choices);
  const installed = useInstalledApps((state) => state.apps);
  const appName = useAppName();
  const entries = Object.entries(choices).sort(([a], [b]) => a.localeCompare(b));
  const describe = (handler: string) => {
    if (handler === 'viewer' || handler === 'download') {
      return t(`fileDefaults.${handler}`);
    }
    const id = handler.slice('app:'.length);
    return installed?.some((app) => app.id === id) === true
      ? appName(id)
      : t('fileDefaults.unavailable');
  };
  return (
    <Dialog
      isOpen
      onOpenChange={(open) => {
        if (!open) {
          onClose();
        }
      }}
      title={t('fileDefaults.title')}
      actions={<Button onPress={onClose}>{t('fileDefaults.close')}</Button>}
    >
      <p>{entries.length === 0 ? t('fileDefaults.empty') : t('fileDefaults.intro')}</p>
      {entries.length > 0 && (
        <ul className={styles.fileDefaults}>
          {entries.map(([extension, handler]) => (
            <li key={extension} className={styles.fileDefault}>
              <span className={styles.fileDefaultExtension}>.{extension}</span>
              <span className={styles.fileDefaultHandler}>{describe(handler)}</span>
              <Button
                aria-label={t('fileDefaults.forgetLabel', { extension })}
                onPress={() => {
                  forgetFileDefault(extension).catch(showError);
                }}
              >
                {t('fileDefaults.forget')}
              </Button>
            </li>
          ))}
        </ul>
      )}
    </Dialog>
  );
}
