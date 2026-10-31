import type { PithyxApp, ShareInfo } from '@pithyx/sdk';
import { Button as AriaButton } from 'react-aria-components';
import { useTranslation } from 'react-i18next';
import { DataTable } from '@pithyx/ui';
import { fileIcon } from '../../items/fileTargets';
import styles from './FilesApp.module.css';
import { nameOf, parentOf } from './paths';
import type { RecentFile } from './recent';

function keyOf(file: RecentFile): string {
  return `${String(file.share)}:${file.path}`;
}

/**
 * The user's recent files, newest first (D394, #333): a double click or Enter opens a file as in its folder, the
 * location shows it in its folder.
 */
export function RecentView({
  files,
  shares,
  format,
  onOpen,
  onShow,
}: {
  files: readonly RecentFile[];
  shares: readonly ShareInfo[];
  format: PithyxApp['format'];
  onOpen: (file: RecentFile) => void;
  onShow: (file: RecentFile) => void;
}) {
  const { t } = useTranslation('files');
  const byKey = new Map(files.map((file) => [keyOf(file), file]));

  function locationOf(file: RecentFile): string {
    const share = shares.find((candidate) => candidate.id === String(file.share))?.name ?? '';
    const folder = parentOf(file.path);
    return folder === '' ? share : `${share}/${folder}`;
  }

  return (
    <DataTable
      aria-label={t('recent.title')}
      className={styles.recent}
      items={files}
      getKey={keyOf}
      selectionMode="single"
      onRowAction={(key) => {
        const file = byKey.get(String(key));
        if (file !== undefined) {
          onOpen(file);
        }
      }}
      emptyState={t('recent.empty')}
      columns={[
        {
          id: 'name',
          label: t('columns.name'),
          sortValue: (file) => nameOf(file.path),
          cell: (file) => {
            const Icon = fileIcon(file.path);
            return (
              <span className={styles.recentName}>
                <Icon className={styles.fileIcon} size={18} stroke={1.75} aria-hidden />
                <span>{nameOf(file.path)}</span>
              </span>
            );
          },
        },
        {
          id: 'location',
          label: t('recent.location'),
          sortValue: locationOf,
          cell: (file) => (
            <AriaButton
              className={styles.recentLocation}
              aria-label={t('recent.show', { location: locationOf(file) })}
              onPress={() => {
                onShow(file);
              }}
            >
              {locationOf(file)}
            </AriaButton>
          ),
        },
        {
          id: 'opened',
          label: t('recent.opened'),
          muted: true,
          width: 170,
          sortValue: (file) => file.openedAt,
          cell: (file) => format.dateTime(file.openedAt),
        },
        {
          id: 'size',
          label: t('columns.size'),
          align: 'end',
          muted: true,
          width: 90,
          sortValue: (file) => file.size,
          cell: (file) => format.bytes(file.size),
        },
      ]}
    />
  );
}
