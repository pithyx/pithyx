import type { PithyxApp } from '@pithyx/sdk';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
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
  type ShareGrants,
  type SubjectGrant,
} from '../sdk/internal';
import { describeProblem } from '../storage/shared';
import { usersCall } from '../sdk/users';
import styles from './Settings.module.css';
import {
  accessOf,
  shareGrantsKey,
  shareListKey,
  useShareGrants,
  useShares,
  withSubjectAccess,
} from './shares';

const LEVELS: readonly ShareAccess[] = ['none', 'read', 'readWrite'];

/** Name and description of a shared folder, and the access of every group and user to it (applied at once, optimistically). */
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
  const grants = useShareGrants(app, shareId);
  const groups = useQuery({
    queryKey: ['groups'],
    queryFn: () => usersCall(app, 'internal.admin.groups.list', {}),
  });
  const key = shareGrantsKey(shareId);
  const change = useMutation({
    mutationFn: (next: SubjectGrant[]) =>
      internalCall(app, 'internal.admin.shares.setGrants', { shareId, grants: next }),
    onMutate: async (next) => {
      await queryClient.cancelQueries({ queryKey: key });
      const previous = queryClient.getQueryData<ShareGrants>(key);
      if (previous !== undefined) {
        queryClient.setQueryData(key, { ...previous, grants: next });
      }
      return { previous };
    },
    onError: (_error, _next, context) => {
      if (context?.previous !== undefined) {
        queryClient.setQueryData(key, context.previous);
      }
    },
    onSuccess: (answer) => {
      queryClient.setQueryData(key, answer);
    },
    onSettled: () => {
      void queryClient.invalidateQueries({ queryKey: shareListKey });
    },
  });
  const current = grants.data?.grants ?? [];
  const select = (kind: SubjectGrant['subjectKind'], id: number, label: string) => (
    <select
      className={styles.select}
      aria-label={t('shares.editDialog.access', { user: label })}
      value={accessOf(current, kind, id)}
      onChange={(event) => {
        change.mutate(withSubjectAccess(current, kind, id, event.target.value as ShareAccess));
      }}
    >
      {LEVELS.map((level) => (
        <option key={level} value={level}>
          {t(`shares.editDialog.levels.${level}`)}
        </option>
      ))}
    </select>
  );

  return (
    <section className={styles.group} aria-labelledby={`rights-${String(shareId)}`}>
      <h3 id={`rights-${String(shareId)}`} className={styles.sectionTitle}>
        {t('shares.editDialog.rights')}
      </h3>
      <p className={styles.muted}>{t('shares.editDialog.rightsIntro')}</p>
      {(grants.isPending || groups.isPending) && (
        <p className={styles.loading} role="status">
          {t('shares.editDialog.loading')}
        </p>
      )}
      {grants.isError && <ErrorNotice error={describeProblem(grants.error)} />}
      {groups.isError && <ErrorNotice error={describeProblem(groups.error)} />}
      {change.isError && <ErrorNotice error={describeProblem(change.error)} />}
      {grants.data !== undefined && groups.data !== undefined && (
        <>
          <h4 className={styles.subTitle}>{t('shares.editDialog.groups')}</h4>
          <ul className={styles.users}>
            {groups.data.map((group) => (
              <li key={`group-${String(group.id)}`} className={styles.user}>
                <span className={styles.userName}>
                  {group.name}
                  {group.builtin && (
                    <span className={styles.path}>{t('shares.editDialog.everybody')}</span>
                  )}
                </span>
                {select('group', group.id, group.name)}
              </li>
            ))}
          </ul>
          <h4 className={styles.subTitle}>{t('shares.editDialog.users')}</h4>
          <ul className={styles.users}>
            {grants.data.users.map((user) => {
              const label = user.displayName ?? user.username;
              const own = accessOf(current, 'user', user.userId);
              return (
                <li key={`user-${String(user.userId)}`} className={styles.user}>
                  <span className={styles.userName}>
                    {label}
                    {user.displayName !== null && (
                      <span className={styles.path}>{user.username}</span>
                    )}
                    {!user.administrator && !user.disabled && user.access !== own && (
                      <span className={styles.path}>
                        {t('shares.editDialog.effective', {
                          access: t(`shares.editDialog.levels.${user.access}`),
                        })}
                      </span>
                    )}
                  </span>
                  {user.administrator ? (
                    <span className={styles.muted}>{t('shares.editDialog.administrator')}</span>
                  ) : (
                    select('user', user.userId, label)
                  )}
                </li>
              );
            })}
          </ul>
        </>
      )}
    </section>
  );
}

type ProtocolFlag = 'smb' | 'sftp' | 'smbEncrypt' | 'webDav' | 'ftp';

/** Whether SMB, SFTP, WebDAV and FTP offer the shared folder, and whether SMB must encrypt; applied at once, optimistically. */
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
      {flag('webDav', t('shares.editDialog.webdav'))}
      {flag('ftp', t('shares.editDialog.ftp'))}
      {change.isError && <ErrorNotice error={describeProblem(change.error)} />}
    </section>
  );
}
