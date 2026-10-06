import { useQuery } from '@tanstack/react-query';
import type { FileVersion, JobInfo, PithyxApp } from '@pithyx/sdk';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, Dialog } from '@pithyx/ui';
import { IconFile, IconFolder, IconRestore } from '../../ui/icons';
import styles from './FilesApp.module.css';

/**
 * "Previous versions" of one file or folder: the versions in the restore points (snapshots) of the share's dataset,
 * newest first.
 * Restoring copies one back as a job, next to the current entry unless the user chose to replace it.
 */
export function VersionsDialog({
  app,
  share,
  path,
  name,
  writable,
  onJob,
  onClose,
}: {
  app: PithyxApp;
  share: string;
  path: string;
  name: string;
  writable: boolean;
  onJob: (job: JobInfo) => void;
  onClose: () => void;
}) {
  const { t } = useTranslation('files');
  const [overwrite, setOverwrite] = useState(false);
  const versions = useQuery({
    queryKey: ['files', share, 'versions', path],
    queryFn: () => app.files.versions({ share }, path),
  });

  async function restore(version: FileVersion) {
    try {
      onJob(await app.files.restoreVersion({ share }, path, version.snapshot, { overwrite }));
      onClose();
    } catch (error) {
      await app.ui.showError(error);
    }
  }

  const list = versions.data ?? [];
  return (
    <Dialog
      isOpen
      isDismissable
      onOpenChange={(open) => {
        if (!open) {
          onClose();
        }
      }}
      title={t('versions.title', { name })}
      actions={<Button onPress={onClose}>{t('versions.close')}</Button>}
    >
      {versions.isPending ? (
        <p>{t('loading')}</p>
      ) : versions.isError ? (
        <p>{t('versions.failed')}</p>
      ) : list.length === 0 ? (
        <p>{t('versions.empty')}</p>
      ) : (
        <>
          <ul className={styles.versions} aria-label={t('versions.list')}>
            {list.map((version) => (
              <li key={version.snapshot} className={styles.version}>
                <span className={styles.name}>
                  {version.kind === 'folder' ? (
                    <IconFolder size={18} stroke={1.75} className={styles.folderIcon} aria-hidden />
                  ) : (
                    <IconFile size={18} stroke={1.75} className={styles.fileIcon} aria-hidden />
                  )}
                  <span>
                    {t('versions.takenOn', { date: app.format.dateTime(version.createdAt) })}
                  </span>
                </span>
                <span className={styles.binMeta}>
                  <span>
                    {t('versions.modifiedOn', { date: app.format.dateTime(version.modified) })}
                  </span>
                  {version.kind === 'file' && <span>{app.format.bytes(version.size)}</span>}
                </span>
                {writable && (
                  <Button
                    size="sm"
                    aria-label={t('versions.restoreOne', {
                      date: app.format.dateTime(version.createdAt),
                    })}
                    onPress={() => {
                      void restore(version);
                    }}
                  >
                    <IconRestore size={16} stroke={1.75} aria-hidden />
                    {t('versions.restore')}
                  </Button>
                )}
              </li>
            ))}
          </ul>
          {writable ? (
            <label className={styles.versionOption}>
              <input
                type="checkbox"
                checked={overwrite}
                onChange={(event) => {
                  setOverwrite(event.currentTarget.checked);
                }}
              />
              {t('versions.overwrite')}
            </label>
          ) : (
            <p className={styles.binNote}>{t('versions.readOnly')}</p>
          )}
          <p className={styles.binNote}>
            {overwrite ? t('versions.overwriteHint') : t('versions.nextToHint')}
          </p>
          <p className={styles.binNote}>{t('versions.note')}</p>
        </>
      )}
    </Dialog>
  );
}
