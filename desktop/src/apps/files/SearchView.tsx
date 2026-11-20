import type { PithyxApp, ShareInfo } from '@pithyx/sdk';
import { Button as AriaButton } from 'react-aria-components';
import { useTranslation } from 'react-i18next';
import { DataTable } from '@pithyx/ui';
import { IconFolder } from '../../ui/icons';
import { fileIcon } from '../../items/fileTargets';
import styles from './FilesApp.module.css';
import { parentOf } from './paths';
import type { SearchHit } from './search';

function keyOf(hit: SearchHit): string {
  return `${String(hit.share)}:${hit.path}`;
}

/**
 * The matches of a search over every place (D413), best first: a double click or Enter opens a file as in its folder
 * and goes into a folder, the location shows the match in its folder.
 */
export function SearchView({
  hits,
  shares,
  query,
  format,
  onOpen,
  onShow,
}: {
  hits: readonly SearchHit[];
  shares: readonly ShareInfo[];
  query: string;
  format: PithyxApp['format'];
  onOpen: (hit: SearchHit) => void;
  onShow: (hit: SearchHit) => void;
}) {
  const { t } = useTranslation('files');
  const byKey = new Map(hits.map((hit) => [keyOf(hit), hit]));

  function locationOf(hit: SearchHit): string {
    const share = shares.find((candidate) => candidate.id === String(hit.share))?.name ?? '';
    const folder = parentOf(hit.path);
    return folder === '' ? share : `${share}/${folder}`;
  }

  return (
    <DataTable
      aria-label={t('find.title', { query })}
      className={styles.recent}
      items={hits}
      getKey={keyOf}
      selectionMode="single"
      onRowAction={(key) => {
        const hit = byKey.get(String(key));
        if (hit !== undefined) {
          onOpen(hit);
        }
      }}
      emptyState={query === '' ? t('find.start') : t('find.empty', { query })}
      columns={[
        {
          id: 'name',
          label: t('columns.name'),
          cell: (hit) => {
            const Icon = hit.kind === 'folder' ? IconFolder : fileIcon(hit.name);
            return (
              <span className={styles.recentName}>
                <Icon className={styles.fileIcon} size={18} stroke={1.75} aria-hidden />
                <span>{hit.name}</span>
              </span>
            );
          },
        },
        {
          id: 'location',
          label: t('recent.location'),
          cell: (hit) => (
            <AriaButton
              className={styles.recentLocation}
              aria-label={t('recent.show', { location: locationOf(hit) })}
              onPress={() => {
                onShow(hit);
              }}
            >
              {locationOf(hit)}
            </AriaButton>
          ),
        },
        {
          id: 'size',
          label: t('columns.size'),
          align: 'end',
          muted: true,
          width: 90,
          cell: (hit) => (hit.kind === 'folder' ? '' : format.bytes(hit.size)),
        },
        {
          id: 'modified',
          label: t('columns.modified'),
          muted: true,
          width: 170,
          cell: (hit) => format.dateTime(hit.modified),
        },
      ]}
    />
  );
}
