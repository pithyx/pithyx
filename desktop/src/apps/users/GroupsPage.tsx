import type { PithyxApp } from '@pithyx/sdk';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import {
  Badge,
  Button,
  Checkbox,
  DataTable,
  TextField,
  Toolbar,
  ToolbarSeparator,
  type DataTableColumn,
} from '@pithyx/ui';
import { IconPencil, IconPlus, IconShieldLock, IconTrash } from '../../ui/icons';
import { describeProblem, usersCall, type GroupSummary, type UserSummary } from '../sdk/users';
import { FormDialog } from './FormDialog';
import { groupsKey, useGroups, usersKey, useUsers } from './queries';
import { RightsDialog } from './RightsDialog';
import styles from './Users.module.css';

type Action =
  | { kind: 'create' }
  | { kind: 'edit'; group: GroupSummary }
  | { kind: 'rights'; group: GroupSummary }
  | { kind: 'delete'; group: GroupSummary };

/**
 * The groups of Settings (D394): the built-in group "users" with everybody, and the groups of the administrators with
 * their members, in a table with **Create group** and the actions on the selected group. Every group is a Linux group
 * with a gid that is never reused.
 */
export function GroupsPanel({ app }: { app: PithyxApp }) {
  const { t } = useTranslation('users');
  const [action, setAction] = useState<Action | null>(null);
  const [selectedId, setSelectedId] = useState<number | null>(null);
  const groups = useGroups(app);
  const users = useUsers(app).data ?? [];
  const names = new Map(users.map((user) => [user.id, user.displayName ?? user.username]));
  const close = () => {
    setAction(null);
  };
  const items = groups.data ?? [];
  const selected = items.find((group) => group.id === selectedId);
  const own = selected !== undefined && !selected.builtin ? selected : undefined;
  const columns: DataTableColumn<GroupSummary>[] = [
    {
      id: 'name',
      label: t('column.name'),
      cell: (group) => (
        <span className={styles.nameCell}>
          <span className={styles.name}>
            {group.name}
            {group.builtin && (
              <>
                {' '}
                <Badge tone="neutral">{t('groups.badge.builtin')}</Badge>
              </>
            )}
          </span>
          <span className={styles.muted}>
            {group.builtin ? t('groups.builtinDescription') : group.description}
          </span>
        </span>
      ),
      sortValue: (group) => group.name,
      isRowHeader: true,
    },
    {
      id: 'members',
      label: t('groups.column.members'),
      cell: (group) =>
        group.builtin
          ? t('groups.everybody', { count: group.memberIds.length })
          : group.memberIds.length === 0
            ? t('groups.noMembers')
            : group.memberIds.map((id) => names.get(id) ?? '').join(', '),
    },
    {
      id: 'gid',
      label: t('groups.column.gid'),
      align: 'end',
      muted: true,
      width: 110,
      cell: (group) => group.id,
      sortValue: (group) => group.id,
    },
  ];
  return (
    <div className={styles.panel}>
      <Toolbar aria-label={t('groups.toolbar')}>
        <Button
          variant="primary"
          onPress={() => {
            setAction({ kind: 'create' });
          }}
        >
          <IconPlus size={18} stroke={1.75} aria-hidden />
          {t('groups.create.button')}
        </Button>
        <ToolbarSeparator />
        <Button
          isDisabled={own === undefined}
          onPress={() => {
            if (own !== undefined) {
              setAction({ kind: 'edit', group: own });
            }
          }}
        >
          <IconPencil size={18} stroke={1.75} aria-hidden />
          {t('edit.button')}
        </Button>
        <Button
          isDisabled={selected === undefined}
          onPress={() => {
            if (selected !== undefined) {
              setAction({ kind: 'rights', group: selected });
            }
          }}
        >
          <IconShieldLock size={18} stroke={1.75} aria-hidden />
          {t('grants.button')}
        </Button>
        <Button
          isDisabled={own === undefined}
          onPress={() => {
            if (own !== undefined) {
              setAction({ kind: 'delete', group: own });
            }
          }}
        >
          <IconTrash size={18} stroke={1.75} aria-hidden />
          {t('delete.button')}
        </Button>
      </Toolbar>
      {groups.isError && <ErrorNotice error={describeProblem(groups.error)} />}
      {groups.data !== undefined && (
        <DataTable
          aria-label={t('groups.title')}
          columns={columns}
          items={items}
          getKey={(group) => group.id}
          density="comfortable"
          selectionMode="single"
          selectedKeys={selected === undefined ? new Set() : new Set([selected.id])}
          onSelectionChange={(keys) => {
            const [first] = keys === 'all' ? [] : [...keys];
            setSelectedId(first === undefined ? null : Number(first));
          }}
          onRowAction={(key) => {
            const group = items.find((entry) => entry.id === Number(key));
            if (group !== undefined && !group.builtin) {
              setAction({ kind: 'edit', group });
            }
          }}
        />
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
    </div>
  );
}

function useGroupChange<T>(run: () => Promise<T>, onClose: () => void) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: run,
    onSuccess: async () => {
      // Memberships show on both lists.
      await client.invalidateQueries({ queryKey: groupsKey });
      await client.invalidateQueries({ queryKey: usersKey });
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
      size="medium"
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
