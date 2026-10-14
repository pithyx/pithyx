import type { PithyxApp } from '@pithyx/sdk';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { internalCall } from '../sdk/internal';
import {
  describeProblem,
  usersCall,
  type GrantEntry,
  type GroupSummary,
  type UserSummary,
} from '../sdk/users';
import { FormDialog } from './FormDialog';
import { GrantEditor } from './GrantEditor';
import styles from './Users.module.css';

export type RightsSubject =
  { kind: 'user'; user: UserSummary } | { kind: 'group'; group: GroupSummary };

export const catalogKey = ['permissions', 'catalog'] as const;

/** The permission catalog and every shared folder, which the grant editors list. */
export function useGrantChoices(app: PithyxApp) {
  const catalog = useQuery({
    queryKey: catalogKey,
    queryFn: () => usersCall(app, 'internal.admin.permissions.catalog', {}),
    staleTime: Infinity,
  });
  const shares = useQuery({
    queryKey: ['settings', 'shares', 'list'],
    queryFn: () => internalCall(app, 'internal.admin.shares.list', {}),
  });
  return { catalog, shares };
}

/** The rights of one user (with what its groups give) or one group, saved as a whole. */
export function RightsDialog({
  app,
  subject,
  onClose,
}: {
  app: PithyxApp;
  subject: RightsSubject;
  onClose: () => void;
}) {
  const { t } = useTranslation('users');
  const client = useQueryClient();
  const { catalog, shares } = useGrantChoices(app);
  const key =
    subject.kind === 'user'
      ? ['grants', 'user', subject.user.id]
      : ['grants', 'group', subject.group.id];
  const current = useQuery({
    queryKey: key,
    queryFn: async () =>
      subject.kind === 'user'
        ? usersCall(app, 'internal.admin.users.grants', { userId: subject.user.id })
        : {
            ...(await usersCall(app, 'internal.admin.groups.grants', {
              groupId: subject.group.id,
            })),
            inherited: [],
          },
  });
  const [edited, setEdited] = useState<GrantEntry[] | undefined>();
  const value = edited ?? current.data?.grants ?? [];
  const save = useMutation({
    mutationFn: () =>
      subject.kind === 'user'
        ? usersCall(app, 'internal.admin.users.setGrants', {
            userId: subject.user.id,
            grants: value,
          })
        : usersCall(app, 'internal.admin.groups.setGrants', {
            groupId: subject.group.id,
            grants: value,
          }),
    onSuccess: async () => {
      // Rights show on the users list, the groups and the shared folders.
      await Promise.all([
        client.invalidateQueries({ queryKey: ['users'] }),
        client.invalidateQueries({ queryKey: ['grants'] }),
        client.invalidateQueries({ queryKey: ['settings', 'shares'] }),
      ]);
      onClose();
    },
  });
  const name =
    subject.kind === 'user'
      ? (subject.user.displayName ?? subject.user.username)
      : subject.group.name;
  const ready =
    catalog.data !== undefined && shares.data !== undefined && current.data !== undefined;
  return (
    <FormDialog
      size="medium"
      title={t(subject.kind === 'user' ? 'grants.userTitle' : 'grants.groupTitle', { name })}
      submit={t('grants.save')}
      disabled={!ready || save.isPending}
      onClose={onClose}
      onSubmit={(event) => {
        event.preventDefault();
        save.mutate();
      }}
    >
      <p className={styles.muted}>
        {t(subject.kind === 'user' ? 'grants.userIntro' : 'grants.groupIntro')}
      </p>
      {(catalog.isError || shares.isError || current.isError) && (
        <ErrorNotice error={describeProblem(catalog.error ?? shares.error ?? current.error)} />
      )}
      {ready && (
        <GrantEditor
          catalog={catalog.data}
          shares={shares.data}
          value={value}
          inherited={current.data.inherited}
          onChange={setEdited}
        />
      )}
      {save.isError && <ErrorNotice error={describeProblem(save.error)} />}
    </FormDialog>
  );
}
