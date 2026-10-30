import type { PithyxApp } from '@pithyx/sdk';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { formatBytes } from '../../desktop/format';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { useFormatLocale } from '../../i18n';
import {
  Button,
  DataTable,
  Switch,
  Toolbar,
  ToolbarSeparator,
  type DataTableColumn,
} from '@pithyx/ui';
import { IconHelpCircle, IconPencil, IconPlus, IconTrash } from '../../ui/icons';
import { internalCall, type ShareDetails } from '../sdk/internal';
import { breakable, describeProblem } from '../storage/shared';
import { CreateShareDialog } from './CreateShareDialog';
import { DeleteShareDialog } from './DeleteShareDialog';
import { EditShareDialog } from './EditShareDialog';
import { RestorePointsDialog } from './RestorePointsDialog';
import styles from './Settings.module.css';
import { shareListKey, useShares } from './shares';

// A delete keeps the share as it was opened: the dialog shows the finished run after the share is gone.
type Open =
  | { kind: 'create' }
  | { kind: 'edit'; shareId: number }
  | { kind: 'delete'; share: ShareDetails }
  | { kind: 'restorePoints' }
  | null;

/**
 * Shared folders: every share with its pool, space, description, rights and restore points; create, edit and delete
 * them.
 */
export function SharesPage({ app }: { app: PithyxApp }) {
  const { t } = useTranslation('settings');
  const shares = useShares(app);
  const [open, setOpen] = useState<Open>(null);
  const [selectedId, setSelectedId] = useState<number | null>(null);
  const close = () => {
    setOpen(null);
  };
  // The edit dialog follows the live list, so it shows the saved values and closes when the share is gone.
  const editing =
    open?.kind === 'edit' ? shares.data?.find((share) => share.id === open.shareId) : undefined;
  const selected = shares.data?.find((share) => share.id === selectedId);

  return (
    <div className={styles.content}>
      <p className={styles.muted}>{t('shares.intro')}</p>
      <Toolbar
        aria-label={t('shares.toolbar')}
        end={
          <Button
            onPress={() => {
              setOpen({ kind: 'restorePoints' });
            }}
          >
            <IconHelpCircle size={18} stroke={1.75} aria-hidden />
            {t('restorePoints.explain')}
          </Button>
        }
      >
        <Button
          variant="primary"
          onPress={() => {
            setOpen({ kind: 'create' });
          }}
        >
          <IconPlus size={18} stroke={1.75} aria-hidden />
          {t('shares.create')}
        </Button>
        <ToolbarSeparator />
        <Button
          isDisabled={selected === undefined}
          onPress={() => {
            if (selected !== undefined) {
              setOpen({ kind: 'edit', shareId: selected.id });
            }
          }}
        >
          <IconPencil size={18} stroke={1.75} aria-hidden />
          {t('shares.edit')}
        </Button>
        <Button
          isDisabled={selected === undefined}
          onPress={() => {
            if (selected !== undefined) {
              setOpen({ kind: 'delete', share: selected });
            }
          }}
        >
          <IconTrash size={18} stroke={1.75} aria-hidden />
          {t('shares.delete')}
        </Button>
      </Toolbar>

      {shares.isPending && (
        <p className={styles.loading} role="status">
          {t('shares.loading')}
        </p>
      )}
      {shares.isError && <ErrorNotice error={describeProblem(shares.error)} />}
      {shares.data !== undefined && (
        <ShareTable
          app={app}
          shares={shares.data}
          selected={selected}
          onSelect={setSelectedId}
          onEdit={(share) => {
            setOpen({ kind: 'edit', shareId: share.id });
          }}
        />
      )}

      {open?.kind === 'create' && <CreateShareDialog app={app} onClose={close} />}
      {editing !== undefined && <EditShareDialog app={app} share={editing} onClose={close} />}
      {open?.kind === 'delete' && (
        <DeleteShareDialog app={app} share={open.share} onClose={close} />
      )}
      {open?.kind === 'restorePoints' && <RestorePointsDialog onClose={close} />}
    </div>
  );
}

/** The one switch per shared folder: daily restore points, kept 7 days. Optimistic, rolled back on error. */
function RestorePointSwitch({ app, share }: { app: PithyxApp; share: ShareDetails }) {
  const { t } = useTranslation('settings');
  const queryClient = useQueryClient();
  const toggle = useMutation({
    mutationFn: (enabled: boolean) =>
      internalCall(app, 'internal.admin.shares.restorePoints', { shareId: share.id, enabled }),
    onMutate: (enabled) => {
      queryClient.setQueryData<ShareDetails[]>(shareListKey, (list) =>
        list?.map((entry) =>
          entry.id === share.id ? { ...entry, restorePoints: enabled } : entry,
        ),
      );
    },
    onSettled: () => {
      void queryClient.invalidateQueries({ queryKey: shareListKey });
    },
  });
  return (
    <>
      <Switch
        isSelected={share.restorePoints === true}
        onChange={(enabled) => {
          toggle.mutate(enabled);
        }}
        aria-label={t('restorePoints.switchLabel', { name: share.name })}
      >
        {share.restorePoints === true ? t('restorePoints.on') : t('restorePoints.off')}
      </Switch>
      {toggle.isError && <ErrorNotice error={describeProblem(toggle.error)} />}
    </>
  );
}

/**
 * The shared folders as a table (D394): name and path, pool, space, description, rights and the restore point switch;
 * a click selects a folder for the toolbar, a double click edits it.
 */
function ShareTable({
  app,
  shares,
  selected,
  onSelect,
  onEdit,
}: {
  app: PithyxApp;
  shares: ShareDetails[];
  selected: ShareDetails | undefined;
  onSelect: (shareId: number | null) => void;
  onEdit: (share: ShareDetails) => void;
}) {
  const { t } = useTranslation('settings');
  const locale = useFormatLocale();
  const bytes = (value: number) => formatBytes(value, locale);
  const used = (share: ShareDetails) =>
    share.dataset === null
      ? null
      : share.usedBytes === null
        ? t('shares.missing')
        : share.quotaBytes !== null
          ? t('shares.usedQuota', {
              used: bytes(share.usedBytes),
              quota: bytes(share.quotaBytes),
            })
          : t('shares.used', {
              used: bytes(share.usedBytes),
              free: bytes(share.availableBytes ?? 0),
            });
  // Four columns without fixed widths, so the table fits a phone without scrolling sideways: the description goes
  // below the name and the space below the pool.
  const columns: DataTableColumn<ShareDetails>[] = [
    {
      id: 'name',
      label: t('shares.columns.name'),
      cell: (share) => (
        <span className={styles.stack}>
          <span className={styles.shareName}>{share.name}</span>{' '}
          <span className={styles.path}>{breakable(share.dataset ?? share.path)}</span>{' '}
          <span className={styles.detail}>
            {share.description === '' ? t('shares.noDescription') : share.description}
          </span>
        </span>
      ),
      sortValue: (share) => share.name,
      isRowHeader: true,
    },
    {
      id: 'pool',
      label: t('shares.columns.pool'),
      cell: (share) => (
        <span className={styles.stack}>
          {share.pool ?? t('shares.folder')} <span className={styles.detail}>{used(share)}</span>
        </span>
      ),
      sortValue: (share) => share.pool ?? '',
    },
    {
      id: 'rights',
      label: t('shares.columns.rights'),
      cell: (share) => (
        <span className={styles.stack}>
          {t('shares.rights', { count: share.readUsers + share.readWriteUsers })}
          {share.readUsers + share.readWriteUsers > 0 && (
            <span className={styles.detail}>
              {t('shares.rightsDetail', {
                read: share.readUsers,
                readWrite: share.readWriteUsers,
              })}
            </span>
          )}
        </span>
      ),
    },
    {
      id: 'restorePoints',
      label: t('shares.columns.restorePoints'),
      cell: (share) =>
        share.restorePoints === null ? (
          <span className={styles.muted}>{t('restorePoints.folder')}</span>
        ) : (
          <RestorePointSwitch app={app} share={share} />
        ),
    },
  ];
  return (
    <DataTable
      aria-label={t('shares.caption')}
      columns={columns}
      items={shares}
      getKey={(share) => share.id}
      density="comfortable"
      selectionMode="single"
      selectedKeys={selected === undefined ? new Set() : new Set([selected.id])}
      onSelectionChange={(keys) => {
        const [first] = keys === 'all' ? [] : [...keys];
        onSelect(first === undefined ? null : Number(first));
      }}
      onRowAction={(key) => {
        const share = shares.find((entry) => entry.id === Number(key));
        if (share !== undefined) {
          onEdit(share);
        }
      }}
      defaultSort={{ column: 'name', direction: 'ascending' }}
      emptyState={t('shares.empty')}
    />
  );
}
