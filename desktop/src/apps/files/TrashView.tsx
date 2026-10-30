import { useInfiniteQuery } from '@tanstack/react-query';
import type { JobInfo, TrashEntry, PithyxApp } from '@pithyx/sdk';
import { useState } from 'react';
import { type Selection as AriaSelection } from 'react-aria-components';
import { useTranslation } from 'react-i18next';
import {
  Button,
  DataTable,
  Dialog,
  Toolbar,
  ToolbarSeparator,
  type DataTableColumn,
} from '@pithyx/ui';
import { IconFile, IconFolder, IconRestore, IconTrash, IconTrashX } from '../../ui/icons';
import styles from './FilesApp.module.css';

export function trashKey(share: string) {
  return ['files', share, 'trash'] as const;
}

/**
 * The recycle bin of one share as a table (D394): what was deleted, from where, when and by whom, and when it goes for
 * good. Restoring is the primary action; restoring, deleting for good and emptying run as jobs, and the last two ask
 * first.
 */
export function TrashView({
  app,
  share,
  shareName,
  writable,
  onJob,
}: {
  app: PithyxApp;
  share: string;
  shareName: string;
  writable: boolean;
  onJob: (job: JobInfo) => void;
}) {
  const { t } = useTranslation('files');
  const [selected, setSelected] = useState<AriaSelection>(new Set());
  const [confirm, setConfirm] = useState<'purge' | 'empty' | null>(null);
  const query = useInfiniteQuery({
    queryKey: trashKey(share),
    queryFn: ({ pageParam }) =>
      app.trash.list({ share }, pageParam === null ? {} : { cursor: pageParam }),
    initialPageParam: null as string | null,
    getNextPageParam: (page) => page.nextCursor,
  });
  const items = query.data?.pages.flatMap((page) => page.items) ?? [];
  const retention = query.data?.pages[0]?.retentionDays;
  const ids =
    selected === 'all'
      ? items.map((item) => item.id)
      : items.filter((item) => selected.has(item.id)).map((item) => item.id);

  async function run(start: () => Promise<JobInfo>) {
    try {
      onJob(await start());
      setSelected(new Set());
    } catch (error) {
      await app.ui.showError(error);
    }
  }

  const columns: DataTableColumn<TrashEntry>[] = [
    {
      id: 'name',
      label: t('columns.name'),
      sortValue: (item) => item.name.toLocaleLowerCase(),
      cell: (item) => (
        <span className={styles.name}>
          {item.kind === 'folder' ? (
            <IconFolder size={18} stroke={1.75} className={styles.folderIcon} aria-hidden />
          ) : (
            <IconFile size={18} stroke={1.75} className={styles.fileIcon} aria-hidden />
          )}
          <span>{item.name}</span>
        </span>
      ),
    },
    {
      id: 'from',
      label: t('bin.columns.from'),
      muted: true,
      sortValue: (item) => item.originalPath,
      cell: (item) => `/${item.originalPath}`,
    },
    {
      id: 'size',
      label: t('columns.size'),
      align: 'end',
      width: 96,
      sortValue: (item) => item.size,
      cell: (item) => app.format.bytes(item.size),
    },
    {
      id: 'deleted',
      label: t('bin.columns.deleted'),
      width: 184,
      sortValue: (item) => item.deletedAt,
      cell: (item) => app.format.dateTime(item.deletedAt),
    },
    {
      id: 'by',
      label: t('bin.columns.by'),
      muted: true,
      width: 120,
      sortValue: (item) => item.deletedBy ?? '',
      cell: (item) => item.deletedBy ?? '',
    },
    {
      id: 'expires',
      label: t('bin.columns.expires'),
      muted: true,
      width: 184,
      sortValue: (item) => item.expiresAt ?? '',
      cell: (item) =>
        item.expiresAt === null ? t('bin.never') : app.format.dateTime(item.expiresAt),
    },
  ];

  return (
    <div className={styles.bin}>
      <Toolbar
        aria-label={t('bin.title', { share: shareName })}
        className={styles.binBar}
        end={
          <p className={styles.binNote}>
            {retention === undefined
              ? t('loading')
              : retention === 0
                ? t('bin.retentionForever')
                : t('bin.retention', { days: retention })}
          </p>
        }
      >
        {writable && (
          <>
            <Button
              size="sm"
              variant="primary"
              isDisabled={ids.length === 0}
              onPress={() => {
                void run(() => app.trash.restore({ share }, ids));
              }}
            >
              <IconRestore size={16} stroke={1.75} aria-hidden />
              {t('bin.restore')}
            </Button>
            <ToolbarSeparator />
            <Button
              size="sm"
              variant="quiet"
              isDisabled={ids.length === 0}
              onPress={() => {
                setConfirm('purge');
              }}
            >
              <IconTrashX size={16} stroke={1.75} aria-hidden />
              {t('bin.delete')}
            </Button>
            <Button
              size="sm"
              variant="quiet"
              isDisabled={items.length === 0}
              onPress={() => {
                setConfirm('empty');
              }}
            >
              <IconTrash size={16} stroke={1.75} aria-hidden />
              {t('bin.emptyBin')}
            </Button>
          </>
        )}
      </Toolbar>
      <div className={styles.scroll}>
        <DataTable
          aria-label={t('bin.title', { share: shareName })}
          columns={columns}
          items={items}
          getKey={(item) => item.id}
          selectionMode={writable ? 'multiple' : 'none'}
          selectedKeys={selected}
          onSelectionChange={setSelected}
          defaultSort={{ column: 'deleted', direction: 'descending' }}
          emptyState={query.isPending ? t('loading') : t('bin.empty')}
        />
        {query.hasNextPage && (
          <Button
            size="sm"
            variant="quiet"
            className={styles.more}
            isDisabled={query.isFetchingNextPage}
            onPress={() => {
              void query.fetchNextPage();
            }}
          >
            {t('bin.more')}
          </Button>
        )}
      </div>
      <Dialog
        isOpen={confirm !== null}
        onOpenChange={(open) => {
          if (!open) {
            setConfirm(null);
          }
        }}
        role="alertdialog"
        tone="danger"
        title={confirm === 'empty' ? t('dialog.emptyTitle') : t('dialog.purgeTitle')}
        actions={
          <>
            <Button
              onPress={() => {
                setConfirm(null);
              }}
            >
              {t('dialog.cancel')}
            </Button>
            <Button
              variant="danger"
              onPress={() => {
                const which = confirm;
                setConfirm(null);
                void run(() =>
                  which === 'empty' ? app.trash.empty({ share }) : app.trash.delete({ share }, ids),
                );
              }}
            >
              {confirm === 'empty' ? t('dialog.emptyConfirm') : t('dialog.purgeConfirm')}
            </Button>
          </>
        }
      >
        <p>
          {confirm === 'empty'
            ? t('dialog.emptyBody', { share: shareName })
            : t('dialog.purgeBody', { count: ids.length })}
        </p>
      </Dialog>
    </div>
  );
}
