import type { PithyxApp } from '@pithyx/sdk';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { Button } from '../../ui/Button';
import { Badge, Checkbox } from '../../ui/Controls';
import { IconPlus } from '../../ui/icons';
import { TextField } from '../../ui/TextField';
import { describeProblem, usersCall, type GroupSummary, type UserSummary } from '../sdk/users';
import { FormDialog } from './FormDialog';
import { RightsDialog } from './RightsDialog';
import styles from './Users.module.css';

export const groupsKey = ['groups'] as const;

export function useGroups(app: PithyxApp) {
  return useQuery({
    queryKey: groupsKey,
    queryFn: () => usersCall(app, 'internal.admin.groups.list', {}),
  });
}

type Action =
  | { kind: 'create' }
  | { kind: 'edit'; group: GroupSummary }
  | { kind: 'rights'; group: GroupSummary }
  | { kind: 'delete'; group: GroupSummary };

/**
 * The groups on the Users page: the built-in group "users" with everybody, and the groups of the administrators with
 * their members, created, edited and deleted here. Every group is a Linux group with a gid that is never reused.
 */
export function GroupsSection({ app, users }: { app: PithyxApp; users: UserSummary[] }) {
  const { t } = useTranslation('users');
  const [action, setAction] = useState<Action | null>(null);
  const groups = useGroups(app);
  const names = new Map(users.map((user) => [user.id, user.displayName ?? user.username]));
  const close = () => {
    setAction(null);
  };
  return (
    <section className={styles.section} aria-labelledby="groups-title">
      <div className={styles.head}>
        <div>
          <h2 id="groups-title" className={styles.title}>
            {t('groups.title')}
          </h2>
          <p className={styles.muted}>{t('groups.intro')}</p>
        </div>
        <Button
          variant="primary"
          onPress={() => {
            setAction({ kind: 'create' });
          }}
        >
          <IconPlus size={16} stroke={1.75} aria-hidden />
          {t('groups.create.button')}
        </Button>
      </div>
      {groups.isError && <ErrorNotice error={describeProblem(groups.error)} />}
      {groups.data !== undefined && (
        <table className={styles.table}>
          <caption className="visually-hidden">{t('groups.title')}</caption>
          <thead>
            <tr>
              <th scope="col">{t('column.name')}</th>
              <th scope="col">{t('groups.column.members')}</th>
              <th scope="col" className={styles.optional}>
                {t('groups.column.gid')}
              </th>
              <th scope="col">
                <span className="visually-hidden">{t('column.actions')}</span>
              </th>
            </tr>
          </thead>
          <tbody>
            {groups.data.map((group) => (
              <tr key={group.id}>
                <th scope="row">
                  <span className={styles.name}>{group.name}</span>
                  {group.builtin && (
                    <span className={styles.badges}>
                      <Badge tone="neutral">{t('groups.badge.builtin')}</Badge>
                    </span>
                  )}
                  <span className={styles.hint}>
                    {group.builtin ? t('groups.builtinDescription') : group.description}
                  </span>
                </th>
                <td>
                  {group.builtin
                    ? t('groups.everybody', { count: group.memberIds.length })
                    : group.memberIds.length === 0
                      ? t('groups.noMembers')
                      : group.memberIds.map((id) => names.get(id) ?? '').join(', ')}
                </td>
                <td className={styles.optional}>{group.id}</td>
                <td>
                  <span className={styles.actions}>
                    <Button
                      size="sm"
                      aria-label={t('groups.rights', { name: group.name })}
                      onPress={() => {
                        setAction({ kind: 'rights', group });
                      }}
                    >
                      {t('grants.button')}
                    </Button>
                    {!group.builtin && (
                      <>
                        <Button
                          size="sm"
                          aria-label={t('groups.edit.label', { name: group.name })}
                          onPress={() => {
                            setAction({ kind: 'edit', group });
                          }}
                        >
                          {t('edit.button')}
                        </Button>
                        <Button
                          size="sm"
                          variant="danger"
                          aria-label={t('groups.delete.label', { name: group.name })}
                          onPress={() => {
                            setAction({ kind: 'delete', group });
                          }}
                        >
                          {t('delete.button')}
                        </Button>
                      </>
                    )}
                  </span>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {action?.kind === 'create' && <GroupDialog app={app} users={users} onClose={close} />}
      {action?.kind === 'edit' && (
        <GroupDialog app={app} users={users} group={action.group} onClose={close} />
      )}
      {action?.kind === 'rights' && (
        <RightsDialog app={app} subject={{ kind: 'group', group: action.group }} onClose={close} />
      )}
      {action?.kind === 'delete' && (
        <DeleteGroupDialog app={app} group={action.group} onClose={close} />
      )}
    </section>
  );
}

function useGroupChange<T>(run: () => Promise<T>, onClose: () => void) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: run,
    onSuccess: async () => {
      // Memberships show on both lists.
      await client.invalidateQueries({ queryKey: groupsKey });
      await client.invalidateQueries({ queryKey: ['users'] });
      onClose();
    },
  });
}

/** Creates a group, or edits one when `group` is given: name, description and members. */
function GroupDialog({
  app,
  users,
  group,
  onClose,
}: {
  app: PithyxApp;
  users: UserSummary[];
  group?: GroupSummary;
  onClose: () => void;
}) {
  const { t } = useTranslation('users');
  const [name, setName] = useState(group?.name ?? '');
  const [description, setDescription] = useState(group?.description ?? '');
  const [memberIds, setMemberIds] = useState<number[]>(group?.memberIds ?? []);
  const save = useGroupChange(
    () =>
      group === undefined
        ? usersCall(app, 'internal.admin.groups.create', { name, description, memberIds })
        : usersCall(app, 'internal.admin.groups.update', {
            groupId: group.id,
            name,
            description,
            memberIds,
          }),
    onClose,
  );
  return (
    <FormDialog
      title={
        group === undefined
          ? t('groups.create.title')
          : t('groups.edit.title', { name: group.name })
      }
      submit={group === undefined ? t('groups.create.submit') : t('edit.submit')}
      onClose={onClose}
      onSubmit={(event) => {
        event.preventDefault();
        save.mutate();
      }}
    >
      {group === undefined && <p className={styles.muted}>{t('groups.create.intro')}</p>}
      <TextField
        label={t('groups.field.name')}
        description={t('field.usernameHint')}
        value={name}
        onChange={setName}
        autoComplete="off"
        autoCapitalize="none"
        isRequired
      />
      <TextField
        label={t('groups.field.description')}
        value={description}
        onChange={setDescription}
        autoComplete="off"
      />
      <fieldset className={styles.fieldset}>
        <legend className={styles.legend}>{t('groups.field.members')}</legend>
        {users.map((user) => (
          <Checkbox
            key={user.id}
            isSelected={memberIds.includes(user.id)}
            onChange={(selected) => {
              setMemberIds(
                selected ? [...memberIds, user.id] : memberIds.filter((id) => id !== user.id),
              );
            }}
          >
            {user.displayName ?? user.username}
          </Checkbox>
        ))}
      </fieldset>
      {save.isError && <ErrorNotice error={describeProblem(save.error)} />}
    </FormDialog>
  );
}

function DeleteGroupDialog({
  app,
  group,
  onClose,
}: {
  app: PithyxApp;
  group: GroupSummary;
  onClose: () => void;
}) {
  const { t } = useTranslation('users');
  const remove = useGroupChange(
    () => usersCall(app, 'internal.admin.groups.delete', { groupId: group.id }),
    onClose,
  );
  return (
    <FormDialog
      title={t('groups.delete.title', { name: group.name })}
      submit={t('groups.delete.submit')}
      danger
      onClose={onClose}
      onSubmit={(event) => {
        event.preventDefault();
        remove.mutate();
      }}
    >
      <p>{t('groups.delete.intro')}</p>
      {remove.isError && <ErrorNotice error={describeProblem(remove.error)} />}
    </FormDialog>
  );
}
