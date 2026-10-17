import type { PithyxApp } from '@pithyx/sdk';
import { Button, TextField } from '@pithyx/ui';
import { useMutation, useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { describeProblem } from '../storage/shared';
import styles from './Store.module.css';

/** The folders of a shared folder at most one page deep; a folder with more than this asks for its path to be typed. */
const FOLDER_PAGE = 1000;

/**
 * Picks a folder in a shared folder for a storage request (B8): the folders of the current one to open, a way up and
 * a new folder, so the administrator neither types a path nor leaves the Store to create it.
 */
export function FolderPicker({
  app,
  shareId,
  shareName,
  path,
  onChange,
}: {
  app: PithyxApp;
  shareId: number;
  shareName: string;
  /** The chosen folder relative to the shared folder, "" for its root. */
  path: string;
  onChange: (path: string) => void;
}) {
  const { t } = useTranslation('store');
  const [creating, setCreating] = useState(false);
  const [name, setName] = useState('');
  const location = { share: String(shareId) };
  const folders = useQuery({
    queryKey: ['store', 'folders', shareId, path],
    queryFn: async () => {
      const page = await app.files.list(location, path, { limit: FOLDER_PAGE });
      return page.entries.filter((entry) => entry.kind === 'folder').map((entry) => entry.name);
    },
  });
  const create = useMutation({
    mutationFn: (folder: string) =>
      app.files.createFolder(location, path === '' ? folder : `${path}/${folder}`),
    onSuccess: (entry) => {
      setCreating(false);
      setName('');
      onChange(path === '' ? entry.name : `${path}/${entry.name}`);
    },
  });
  const parts = path === '' ? [] : path.split('/');
  const submit = () => {
    if (name.trim() !== '' && !create.isPending) {
      create.mutate(name.trim());
    }
  };

  return (
    <div className={styles.field}>
      <span className={styles.fieldLabel}>{t('detail.storage.path')}</span>
      <p className={styles.folderPath}>
        {[shareName, ...parts].join(' / ')}
        {parts.length === 0 && <span className={styles.muted}> ({t('detail.storage.root')})</span>}
      </p>
      <div className={styles.folderActions}>
        <Button
          isDisabled={parts.length === 0}
          onPress={() => {
            onChange(parts.slice(0, -1).join('/'));
          }}
        >
          {t('detail.storage.up')}
        </Button>
        <Button
          onPress={() => {
            setCreating(true);
            create.reset();
          }}
        >
          {t('detail.storage.newFolder')}
        </Button>
      </div>
      {creating && (
        // Inside the mapping dialog's form: Enter creates the folder instead of saving the mapping.
        <div className={styles.folderActions}>
          <TextField
            label={t('detail.storage.newFolderName')}
            value={name}
            onChange={setName}
            onKeyDown={(event) => {
              if (event.key === 'Enter') {
                event.preventDefault();
                submit();
              }
            }}
            autoFocus
            autoCapitalize="none"
            autoComplete="off"
            spellCheck="false"
          />
          <Button
            variant="primary"
            isDisabled={name.trim() === '' || create.isPending}
            onPress={submit}
          >
            {t('detail.storage.create')}
          </Button>
        </div>
      )}
      {create.isError && <ErrorNotice error={describeProblem(create.error)} />}
      {folders.isPending && (
        <p className={styles.loading} role="status">
          {t('detail.storage.loadingFolders')}
        </p>
      )}
      {folders.isError && <ErrorNotice error={describeProblem(folders.error)} />}
      {folders.data !== undefined &&
        (folders.data.length === 0 ? (
          <p className={styles.muted}>{t('detail.storage.noFolders')}</p>
        ) : (
          <ul className={styles.folderList} aria-label={t('detail.storage.folders')}>
            {folders.data.map((folder) => (
              <li key={folder}>
                <Button
                  variant="quiet"
                  onPress={() => {
                    onChange(path === '' ? folder : `${path}/${folder}`);
                  }}
                >
                  {folder}
                </Button>
              </li>
            ))}
          </ul>
        ))}
    </div>
  );
}
