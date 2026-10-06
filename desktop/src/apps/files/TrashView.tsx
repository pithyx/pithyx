import { useInfiniteQuery } from '@tanstack/react-query';
import type { JobInfo, TrashEntry, PithyxApp } from '@pithyx/sdk';
import { useState } from 'react';
import { ListBox, ListBoxItem, type Selection as AriaSelection } from 'react-aria-components';
import { useTranslation } from 'react-i18next';
import { Button, Dialog } from '@pithyx/ui';
import { IconFile, IconFolder, IconRestore, IconTrashX } from '../../ui/icons';
import styles from './FilesApp.module.css';

export function trashKey(share: string) {
  return ['files', share, 'trash'] as const;
}

/**
 * The recycle bin of one share: what was deleted, from where, when, and when it goes for good. Restoring, deleting
 * for good and emptying run as jobs; the last two ask first.
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

  return (
    <div className={styles.bin}>
      <div className={styles.binBar}>
        <p className={styles.binNote}>
          {retention === undefined
            ? t('loading')
            : retention === 0
              ? t('bin.retentionForever')
              : t('bin.retention', { days: retention })}
        </p>
        {writable && (
          <>
            <Button
              size="sm"
              isDisabled={ids.length === 0}
              onPress={() => {
                void run(() => app.trash.restore({ share }, ids));
              }}
            >
              <IconRestore size={16} stroke={1.75} aria-hidden />
              {t('bin.restore')}
            </Button>
            <Button
              size="sm"
              variant="danger"
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
              variant="danger"
              isDisabled={items.length === 0}
              onPress={() => {
                setConfirm('empty');
              }}
            >
              {t('bin.emptyBin')}
            </Button>
          </>
        )}
      </div>
      <div className={styles.scroll}>
        <ListBox
          aria-label={t('bin.title', { share: shareName })}
          selectionMode="multiple"
          selectedKeys={selected}
          onSelectionChange={setSelected}
          className={styles.binList}
          renderEmptyState={() => (
            <p className={styles.empty}>{query.isPending ? t('loading') : t('bin.empty')}</p>
          )}
          items={items}
        >
          {(item: TrashEntry) => (
            <ListBoxItem id={item.id} textValue={item.name} className={styles.binRow}>
              <span className={styles.name}>
                {item.kind === 'folder' ? (
                  <IconFolder size={18} stroke={1.75} className={styles.folderIcon} aria-hidden />
                ) : (
                  <IconFile size={18} stroke={1.75} className={styles.fileIcon} aria-hidden />
                )}
                <span>{item.name}</span>
              </span>
              <span className={styles.binMeta}>
                <span>{t('bin.fromPath', { path: `/${item.originalPath}` })}</span>
                <span>{t('bin.deletedOn', { date: app.format.dateTime(item.deletedAt) })}</span>
                <span className={styles.optional}>
                  {item.expiresAt === null
                    ? t('bin.never')
                    : t('bin.expiresOn', { date: app.format.dateTime(item.expiresAt) })}
                </span>
              </span>
            </ListBoxItem>
          )}
        </ListBox>
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
