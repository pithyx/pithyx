import type { PithyxApp } from '@pithyx/sdk';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { formatBytes } from '../../desktop/format';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { useFormatLocale } from '../../i18n';
import { Button } from '../../ui/Button';
import { Switch } from '../../ui/Controls';
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
  const close = () => {
    setOpen(null);
  };
  // The edit dialog follows the live list, so it shows the saved values and closes when the share is gone.
  const editing =
    open?.kind === 'edit' ? shares.data?.find((share) => share.id === open.shareId) : undefined;

  return (
    <div className={styles.content}>
      <header className={styles.pageHeader}>
        <div>
          <h2 className={styles.pageTitle}>{t('shares.title')}</h2>
          <p className={styles.muted}>{t('shares.intro')}</p>
        </div>
        <div className={styles.buttons}>
          <Button
            onPress={() => {
              setOpen({ kind: 'restorePoints' });
            }}
          >
            <IconHelpCircle size={16} aria-hidden />
            {t('restorePoints.explain')}
          </Button>
          <Button
            variant="primary"
            onPress={() => {
              setOpen({ kind: 'create' });
            }}
          >
            <IconPlus size={16} aria-hidden />
            {t('shares.create')}
          </Button>
        </div>
      </header>

      {shares.isPending && (
        <p className={styles.loading} role="status">
          {t('shares.loading')}
        </p>
      )}
      {shares.isError && <ErrorNotice error={describeProblem(shares.error)} />}
      {shares.data?.length === 0 && <p className={styles.muted}>{t('shares.empty')}</p>}
      {shares.data !== undefined && shares.data.length > 0 && (
        <ShareTable
          app={app}
          shares={shares.data}
          onEdit={(share) => {
            setOpen({ kind: 'edit', shareId: share.id });
          }}
          onDelete={(share) => {
            setOpen({ kind: 'delete', share });
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

function ShareTable({
  app,
  shares,
  onEdit,
  onDelete,
}: {
  app: PithyxApp;
  shares: ShareDetails[];
  onEdit: (share: ShareDetails) => void;
  onDelete: (share: ShareDetails) => void;
}) {
  const { t } = useTranslation('settings');
  const locale = useFormatLocale();
  const bytes = (value: number) => formatBytes(value, locale);
  return (
    <table className={styles.table}>
      <caption className="visually-hidden">{t('shares.caption')}</caption>
      <thead>
        <tr>
          <th scope="col">{t('shares.columns.name')}</th>
          <th scope="col">{t('shares.columns.pool')}</th>
          <th scope="col">{t('shares.columns.used')}</th>
          <th scope="col">{t('shares.columns.description')}</th>
          <th scope="col">{t('shares.columns.rights')}</th>
          <th scope="col">{t('shares.columns.restorePoints')}</th>
          <th scope="col">
            <span className="visually-hidden">{t('shares.columns.actions')}</span>
          </th>
        </tr>
      </thead>
      <tbody>
        {shares.map((share) => (
          <tr key={share.id}>
            <th scope="row" className={styles.shareName}>
              {share.name}
              <span className={styles.path}>{breakable(share.dataset ?? share.path)}</span>
            </th>
            <td data-label={t('shares.columns.pool')}>{share.pool ?? t('shares.folder')}</td>
            <td data-label={t('shares.columns.used')}>
              {share.dataset === null
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
                      })}
            </td>
            <td data-label={t('shares.columns.description')}>
              {share.description === '' ? (
                <span className={styles.muted}>{t('shares.noDescription')}</span>
              ) : (
                share.description
              )}
            </td>
            <td data-label={t('shares.columns.rights')}>
              {t('shares.rights', { count: share.readUsers + share.readWriteUsers })}
              {share.readUsers + share.readWriteUsers > 0 && (
                <span className={styles.detail}>
                  {t('shares.rightsDetail', {
                    read: share.readUsers,
                    readWrite: share.readWriteUsers,
                  })}
                </span>
              )}
            </td>
            <td data-label={t('shares.columns.restorePoints')}>
              {share.restorePoints === null ? (
                <span className={styles.muted}>{t('restorePoints.folder')}</span>
              ) : (
                <RestorePointSwitch app={app} share={share} />
              )}
            </td>
            <td className={styles.rowActions}>
              <Button
                size="sm"
                aria-label={t('shares.editLabel', { name: share.name })}
                onPress={() => {
                  onEdit(share);
                }}
              >
                <IconPencil size={16} aria-hidden />
                {t('shares.edit')}
              </Button>
              <Button
                size="sm"
                variant="quiet"
                aria-label={t('shares.deleteLabel', { name: share.name })}
                onPress={() => {
                  onDelete(share);
                }}
              >
                <IconTrash size={16} aria-hidden />
                {t('shares.delete')}
              </Button>
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}
