import type { PithyxApp } from '@pithyx/sdk';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { Button } from '../../ui/Button';
import { Switch } from '../../ui/Controls';
import { Dialog } from '../../ui/Dialog';
import { TextField } from '../../ui/TextField';
import {
  internalCall,
  type ShareAccess,
  type ShareDetails,
  type ShareUserAccess,
} from '../sdk/internal';
import { describeProblem } from '../storage/shared';
import styles from './Settings.module.css';
import { shareAccessKey, shareListKey, useShareAccess, useShares, withAccess } from './shares';

const LEVELS: readonly ShareAccess[] = ['none', 'read', 'readWrite'];

/** Name and description of a shared folder, and every user's access to it (applied at once, optimistically). */
export function EditShareDialog({
  app,
  share,
  onClose,
}: {
  app: PithyxApp;
  share: ShareDetails;
  onClose: () => void;
}) {
  const { t } = useTranslation('settings');
  const queryClient = useQueryClient();
  const [name, setName] = useState(share.name);
  const [description, setDescription] = useState(share.description);
  const save = useMutation({
    mutationFn: () =>
      internalCall(app, 'internal.admin.shares.update', {
        shareId: share.id,
        ...(name.trim() === share.name ? {} : { name }),
        ...(description.trim() === share.description ? {} : { description }),
      }),
    onSuccess: (updated) => {
      queryClient.setQueryData<ShareDetails[]>(shareListKey, (list) =>
        list?.map((entry) => (entry.id === updated.id ? updated : entry)),
      );
    },
  });
  const changed = name.trim() !== share.name || description.trim() !== share.description;

  return (
    <Dialog
      isOpen
      onOpenChange={(open) => {
        if (!open) {
          onClose();
        }
      }}
      title={t('shares.editDialog.title', { name: share.name })}
      actions={<Button onPress={onClose}>{t('shares.close')}</Button>}
    >
      <div className={styles.form}>
        <form
          className={styles.group}
          aria-label={t('shares.editDialog.details')}
          onSubmit={(event) => {
            event.preventDefault();
            if (changed) {
              save.mutate();
            }
          }}
        >
          <TextField
            label={t('shares.editDialog.name')}
            {...(share.dataset === null ? {} : { description: t('shares.editDialog.datasetKept') })}
            value={name}
            onChange={(value) => {
              setName(value);
              save.reset();
            }}
            autoComplete="off"
            maxLength={64}
          />
          <TextField
            label={t('shares.editDialog.description')}
            value={description}
            onChange={(value) => {
              setDescription(value);
              save.reset();
            }}
            autoComplete="off"
            maxLength={200}
          />
          <div className={styles.buttons}>
            <Button type="submit" variant="primary" isDisabled={!changed || save.isPending}>
              {t('shares.editDialog.save')}
            </Button>
            {save.isSuccess && (
              <p className={styles.muted} role="status">
                {t('shares.editDialog.saved')}
              </p>
            )}
          </div>
          {save.isError && <ErrorNotice error={describeProblem(save.error)} />}
        </form>
        {share.dataset !== null && <ShareProtocols app={app} share={share} />}
        <ShareRights app={app} shareId={share.id} />
      </div>
    </Dialog>
  );
}

function ShareRights({ app, shareId }: { app: PithyxApp; shareId: number }) {
  const { t } = useTranslation('settings');
  const queryClient = useQueryClient();
  const users = useShareAccess(app, shareId);
  const key = shareAccessKey(shareId);
  const change = useMutation({
    mutationFn: ({ userId, access }: { userId: number; access: ShareAccess }) =>
      internalCall(app, 'internal.admin.shares.setAccess', { shareId, userId, access }),
    onMutate: async ({ userId, access }) => {
      await queryClient.cancelQueries({ queryKey: key });
      const previous = queryClient.getQueryData<ShareUserAccess[]>(key);
      if (previous !== undefined) {
        queryClient.setQueryData(key, withAccess(previous, userId, access));
      }
      return { previous };
    },
    onError: (_error, _change, context) => {
      if (context?.previous !== undefined) {
        queryClient.setQueryData(key, context.previous);
      }
    },
    onSettled: () => {
      void queryClient.invalidateQueries({ queryKey: key });
      void queryClient.invalidateQueries({ queryKey: shareListKey });
    },
  });

  return (
    <section className={styles.group} aria-labelledby={`rights-${String(shareId)}`}>
      <h3 id={`rights-${String(shareId)}`} className={styles.sectionTitle}>
        {t('shares.editDialog.rights')}
      </h3>
      <p className={styles.muted}>{t('shares.editDialog.rightsIntro')}</p>
      {users.isPending && (
        <p className={styles.loading} role="status">
          {t('shares.editDialog.loading')}
        </p>
      )}
      {users.isError && <ErrorNotice error={describeProblem(users.error)} />}
      {change.isError && <ErrorNotice error={describeProblem(change.error)} />}
      {users.data !== undefined && (
        <ul className={styles.users}>
          {users.data.map((user) => {
            const label = user.displayName ?? user.username;
            return (
              <li key={user.userId} className={styles.user}>
                <span className={styles.userName}>
                  {label}
                  {user.displayName !== null && (
                    <span className={styles.path}>{user.username}</span>
                  )}
                </span>
                {user.administrator ? (
                  <span className={styles.muted}>{t('shares.editDialog.administrator')}</span>
                ) : (
                  <select
                    className={styles.select}
                    aria-label={t('shares.editDialog.access', { user: label })}
                    value={user.access}
                    onChange={(event) => {
                      change.mutate({
                        userId: user.userId,
                        access: event.target.value as ShareAccess,
                      });
                    }}
                  >
                    {LEVELS.map((level) => (
                      <option key={level} value={level}>
                        {t(`shares.editDialog.levels.${level}`)}
                      </option>
                    ))}
                  </select>
                )}
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}

type ProtocolFlag = 'smb' | 'sftp' | 'smbEncrypt';

/** Whether SMB and SFTP offer the shared folder, and whether SMB must encrypt; applied at once, optimistically. */
function ShareProtocols({ app, share }: { app: PithyxApp; share: ShareDetails }) {
  const { t } = useTranslation('settings');
  const queryClient = useQueryClient();
  const change = useMutation({
    mutationFn: ({ flag, value }: { flag: ProtocolFlag; value: boolean }) =>
      internalCall(app, 'internal.admin.shares.update', { shareId: share.id, [flag]: value }),
    onMutate: ({ flag, value }) => {
      queryClient.setQueryData<ShareDetails[]>(shareListKey, (list) =>
        list?.map((entry) => (entry.id === share.id ? { ...entry, [flag]: value } : entry)),
      );
    },
    onSettled: () => {
      void queryClient.invalidateQueries({ queryKey: shareListKey });
    },
  });
  // The list query, so the switches follow the optimistic change.
  const current = useShares(app).data?.find((entry) => entry.id === share.id) ?? share;
  const flag = (name: ProtocolFlag, label: string) => (
    <Switch
      isSelected={current[name]}
      isDisabled={name === 'smbEncrypt' && !current.smb}
      onChange={(value) => {
        change.mutate({ flag: name, value });
      }}
    >
      {label}
    </Switch>
  );

  return (
    <section className={styles.group} aria-labelledby={`protocols-${String(share.id)}`}>
      <h3 id={`protocols-${String(share.id)}`} className={styles.sectionTitle}>
        {t('shares.editDialog.protocols')}
      </h3>
      <p className={styles.muted}>{t('shares.editDialog.protocolsIntro')}</p>
      {flag('smb', t('shares.editDialog.smb'))}
      {flag('smbEncrypt', t('shares.editDialog.smbEncrypt'))}
      <p className={styles.muted}>{t('shares.editDialog.smbEncryptHint')}</p>
      {flag('sftp', t('shares.editDialog.sftp'))}
      {change.isError && <ErrorNotice error={describeProblem(change.error)} />}
    </section>
  );
}
