import type { PithyxApp } from '@pithyx/sdk';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { Fragment, useState, type SyntheticEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { NewPasswordField } from '../../auth/password/NewPasswordField';
import { useNewPasswordCheck } from '../../auth/password/PasswordStrengthMeter';
import { ErrorNotice } from '../../errors/ErrorNotice';
import {
  Badge,
  Button,
  Checkbox,
  DataTable,
  SearchField,
  StatusBadge,
  TextField,
  Toolbar,
  ToolbarSeparator,
  type DataTableColumn,
} from '@pithyx/ui';
import { initialsOf } from '../../auth/parts';
import {
  IconCircleCheck,
  IconKey,
  IconLock,
  IconPencil,
  IconShieldLock,
  IconTrash,
  IconUserPlus,
} from '../../ui/icons';
import {
  describeProblem,
  usersCall,
  type GrantEntry,
  type GroupSummary,
  type HomeChoice,
  type UserSummary,
} from '../sdk/users';
import { useShares } from '../settings/shares';
import { FormDialog } from './FormDialog';
import { GrantEditor, protocolKey, type ProtocolName } from './GrantEditor';
import { groupsKey, useGroups, usersKey, useUsers } from './queries';
import { RightsDialog, useGrantChoices } from './RightsDialog';
import styles from './Users.module.css';

type Action =
  | { kind: 'create' }
  | { kind: 'edit'; user: UserSummary }
  | { kind: 'rights'; user: UserSummary }
  | { kind: 'password'; user: UserSummary }
  | { kind: 'delete'; user: UserSummary };

const RIGHTS = ['desktopLogin', 'sshLogin', 'power'] as const;

const PROTOCOL_NAMES: Record<ProtocolName, string> = {
  smb: 'SMB',
  sftp: 'SFTP',
  webdav: 'WebDAV',
  ftp: 'FTP',
};

/**
 * The users of Settings (D394): a toolbar with **Create user** and the actions on the selected user, a table with the
 * name, groups, second factor, file services and state, and the selected user's details on the right.
 */
export function UsersPanel({ app }: { app: PithyxApp }) {
  const { t } = useTranslation('users');
  const users = useUsers(app);
  const [action, setAction] = useState<Action | null>(null);
  const [selectedId, setSelectedId] = useState<number | null>(null);
  const [search, setSearch] = useState('');
  const groups = useGroups(app);
  const groupNames = new Map((groups.data ?? []).map((group) => [group.id, group.name]));
  const close = () => {
    setAction(null);
  };
  const wanted = search.trim().toLowerCase();
  const items = (users.data ?? []).filter(
    (user) =>
      wanted === '' ||
      user.username.toLowerCase().includes(wanted) ||
      (user.displayName ?? '').toLowerCase().includes(wanted),
  );
  const selected = items.find((user) => user.id === selectedId);
  const columns: DataTableColumn<UserSummary>[] = [
    {
      id: 'name',
      label: t('column.name'),
      cell: (user) => (
        <span className={styles.nameCell}>
          <span className={styles.name}>{user.displayName ?? user.username}</span>
          {user.displayName !== null && <span className={styles.muted}>{user.username}</span>}
        </span>
      ),
      sortValue: (user) => user.displayName ?? user.username,
      isRowHeader: true,
    },
    {
      id: 'groups',
      label: t('column.groups'),
      cell: (user) => (
        <span className={styles.badges}>
          {user.isAdmin && <Badge tone="accent">{t('badge.admin')}</Badge>}
          {user.groupIds.map((id) => (
            <Badge key={id} tone="neutral">
              {groupNames.get(id) ?? ''}
            </Badge>
          ))}
        </span>
      ),
    },
    {
      id: 'services',
      label: t('fileServices.title'),
      hideWhenNarrow: true,
      muted: true,
      // The protocols' own names, short enough for a column; the details pane explains them.
      cell: (user) =>
        user.protocols.map((protocol) => PROTOCOL_NAMES[protocolKey(protocol)]).join(', '),
    },
    {
      id: 'state',
      label: t('column.state'),
      // Two-factor sign-in sits below the state, so the table keeps four columns on a phone.
      cell: (user) => (
        <span className={styles.stateCell}>
          {user.disabled ? (
            <StatusBadge tone="neutral">{t('badge.disabled')}</StatusBadge>
          ) : (
            <StatusBadge tone="success">{t('state.active')}</StatusBadge>
          )}
          {user.totpEnabled ? (
            <span className={styles.check}>
              <IconCircleCheck size={16} stroke={1.75} aria-hidden />
              {t('twoFactor.on')}
            </span>
          ) : (
            <span className={styles.muted}>{t('twoFactor.off')}</span>
          )}
        </span>
      ),
      sortValue: (user) => (user.disabled ? 1 : 0),
    },
  ];
  return (
    <div className={styles.panel}>
      <Toolbar
        aria-label={t('toolbar')}
        end={
          <SearchField
            label={t('search')}
            placeholder={t('search')}
            value={search}
            onChange={setSearch}
          />
        }
      >
        <Button
          variant="primary"
          onPress={() => {
            setAction({ kind: 'create' });
          }}
        >
          <IconUserPlus size={18} stroke={1.75} aria-hidden />
          {t('create.button')}
        </Button>
        <ToolbarSeparator />
        <Button
          isDisabled={selected === undefined}
          onPress={() => {
            if (selected !== undefined) {
              setAction({ kind: 'edit', user: selected });
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
              setAction({ kind: 'rights', user: selected });
            }
          }}
        >
          <IconShieldLock size={18} stroke={1.75} aria-hidden />
          {t('grants.button')}
        </Button>
        <Button
          isDisabled={selected === undefined}
          onPress={() => {
            if (selected !== undefined) {
              setAction({ kind: 'password', user: selected });
            }
          }}
        >
          <IconKey size={18} stroke={1.75} aria-hidden />
          {t('password.button')}
        </Button>
        <DisableButton app={app} user={selected} />
        <Button
          isDisabled={selected === undefined}
          onPress={() => {
            if (selected !== undefined) {
              setAction({ kind: 'delete', user: selected });
            }
          }}
        >
          <IconTrash size={18} stroke={1.75} aria-hidden />
          {t('delete.button')}
        </Button>
      </Toolbar>
      {users.isError && <ErrorNotice error={describeProblem(users.error)} />}
      {users.data !== undefined && (
        <DataTable
          aria-label={t('title')}
          columns={columns}
          items={items}
          getKey={(user) => user.id}
          density="comfortable"
          selectionMode="single"
          selectedKeys={selected === undefined ? new Set() : new Set([selected.id])}
          onSelectionChange={(keys) => {
            const [first] = keys === 'all' ? [] : [...keys];
            setSelectedId(first === undefined ? null : Number(first));
          }}
          onRowAction={(key) => {
            const user = items.find((entry) => entry.id === Number(key));
            if (user !== undefined) {
              setAction({ kind: 'edit', user });
            }
          }}
          defaultSort={{ column: 'name', direction: 'ascending' }}
          emptyState={t('noMatch')}
          details={
            selected === undefined ? undefined : (
              <UserDetails
                user={selected}
                groupNames={groupNames}
                onRights={() => {
                  setAction({ kind: 'rights', user: selected });
                }}
              />
            )
          }
          detailsLabel={t('details.label')}
        />
      )}
      {action?.kind === 'create' && (
        <CreateDialog app={app} groups={groups.data ?? []} onClose={close} />
      )}
      {action?.kind === 'edit' && (
        <EditDialog app={app} user={action.user} groups={groups.data ?? []} onClose={close} />
      )}
      {action?.kind === 'rights' && (
        <RightsDialog app={app} subject={{ kind: 'user', user: action.user }} onClose={close} />
      )}
      {action?.kind === 'password' && (
        <PasswordDialog app={app} user={action.user} onClose={close} />
      )}
      {action?.kind === 'delete' && <DeleteDialog app={app} user={action.user} onClose={close} />}
    </div>
  );
}

/** The selected user: name, groups, home, file services and special rights, with **Rights** to change them. */
function UserDetails({
  user,
  groupNames,
  onRights,
}: {
  user: UserSummary;
  groupNames: Map<number, string>;
  onRights: () => void;
}) {
  const { t } = useTranslation('users');
  const name = user.displayName ?? user.username;
  const has = (right: string) => user.isAdmin || user.rights.includes(`right:${right}`);
  return (
    <div className={styles.details}>
      <div className={styles.detailsHead}>
        <span className={styles.avatar} aria-hidden>
          {initialsOf(name)}
        </span>
        <span className={styles.nameCell}>
          <span className={styles.name}>{name}</span>
          <span className={styles.muted}>
            {[
              ...(user.displayName === null ? [] : [user.username]),
              user.isAdmin ? t('badge.admin') : t('details.user'),
            ].join(' · ')}
          </span>
        </span>
      </div>
      <h3 className={styles.detailsTitle}>{t('details.account')}</h3>
      <dl className={styles.facts}>
        <dt>{t('column.groups')}</dt>
        <dd>
          {user.groupIds.length === 0
            ? t('details.noGroups')
            : user.groupIds.map((id) => groupNames.get(id) ?? '').join(', ')}
        </dd>
        <dt>{t('column.uid')}</dt>
        <dd>{user.uid ?? ''}</dd>
        <dt>{t('column.home')}</dt>
        <dd className={styles.path}>{user.home ?? t('noHome')}</dd>
      </dl>
      <h3 className={styles.detailsTitle}>{t('fileServices.title')}</h3>
      {user.protocols.length === 0 ? (
        <p className={styles.muted}>{t('details.noServices')}</p>
      ) : (
        <span className={styles.badges}>
          {user.protocols.map((protocol) => (
            <Badge key={protocol} tone="neutral">
              {t(`grants.protocol.${protocolKey(protocol)}`)}
            </Badge>
          ))}
        </span>
      )}
      {!user.disabled && user.protocols.length > 0 && !user.fileServicePassword && (
        <p className={styles.hint}>{t('fileServices.signInOnce')}</p>
      )}
      <h3 className={styles.detailsTitle}>{t('grants.rights')}</h3>
      <dl className={styles.facts}>
        {RIGHTS.map((right) => (
          <Fragment key={right}>
            <dt>{t(`grants.right.${right}`)}</dt>
            <dd>{has(right) ? t('details.yes') : t('details.no')}</dd>
          </Fragment>
        ))}
      </dl>
      <div>
        <Button size="sm" onPress={onRights}>
          <IconShieldLock size={16} stroke={1.75} aria-hidden />
          {t('details.editRights')}
        </Button>
      </div>
    </div>
  );
}

/** Deactivates or activates the selected user at once; the daemon refuses it for oneself and the last active administrator. */
function DisableButton({ app, user }: { app: PithyxApp; user: UserSummary | undefined }) {
  const { t } = useTranslation('users');
  const client = useQueryClient();
  const toggle = useMutation({
    mutationFn: (target: UserSummary) =>
      usersCall(app, 'internal.admin.users.update', {
        userId: target.id,
        disabled: !target.disabled,
      }),
    onSuccess: async () => {
      await client.invalidateQueries({ queryKey: usersKey });
    },
  });
  return (
    <>
      <Button
        isDisabled={user === undefined || toggle.isPending}
        onPress={() => {
          if (user !== undefined) {
            toggle.mutate(user);
          }
        }}
      >
        <IconLock size={18} stroke={1.75} aria-hidden />
        {user?.disabled === true ? t('disable.enable') : t('disable.disable')}
      </Button>
      {toggle.isError && <ErrorNotice error={describeProblem(toggle.error)} />}
    </>
  );
}

function useChange<T, V = void>(run: (value: V) => Promise<T>, onClose: () => void) {
  const client = useQueryClient();
  return useMutation({
    mutationFn: run,
    onSuccess: async () => {
      // Memberships show on both lists.
      await client.invalidateQueries({ queryKey: usersKey });
      await client.invalidateQueries({ queryKey: groupsKey });
      onClose();
    },
  });
}

function CreateDialog({
  app,
  groups,
  onClose,
}: {
  app: PithyxApp;
  groups: GroupSummary[];
  onClose: () => void;
}) {
  const { t } = useTranslation('users');
  const checkPassword = useNewPasswordCheck();
  const [username, setUsername] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [password, setPassword] = useState('');
  const { catalog, shares } = useGrantChoices(app);
  const [edited, setEdited] = useState<GrantEntry[] | undefined>();
  // A new user may sign in, use SMB and open the apps unless the administrator changes it.
  const grants =
    edited ??
    [
      'right:desktopLogin',
      'protocol:smb',
      ...(catalog.data?.apps ?? []).map((id) => `app:${id}`),
    ].map((resource): GrantEntry => ({ resource, level: 'allow' }));
  const [groupIds, setGroupIds] = useState<number[]>([]);
  const [passwordError, setPasswordError] = useState<string | undefined>();
  const create = useChange(
    () =>
      usersCall(app, 'internal.admin.users.create', {
        username,
        password,
        displayName: displayName.trim() === '' ? null : displayName,
        groupIds,
        grants,
      }),
    onClose,
  );
  function submit(event: SyntheticEvent<HTMLFormElement>) {
    event.preventDefault();
    const problem = checkPassword(password);
    setPasswordError(problem);
    if (problem === undefined) {
      create.mutate();
    }
  }
  return (
    <FormDialog
      size="medium"
      title={t('create.title')}
      submit={t('create.submit')}
      onClose={onClose}
      onSubmit={submit}
    >
      <p className={styles.muted}>{t('create.intro')}</p>
      <TextField
        label={t('field.username')}
        description={t('field.usernameHint')}
        value={username}
        onChange={setUsername}
        autoComplete="off"
        autoCapitalize="none"
        isRequired
      />
      <TextField
        label={t('field.displayName')}
        value={displayName}
        onChange={setDisplayName}
        autoComplete="off"
      />
      <NewPasswordField
        label={t('field.password')}
        name="password"
        error={passwordError}
        onChange={(event) => {
          setPassword(event.target.value);
          setPasswordError(undefined);
        }}
        required
      />
      <Memberships groups={groups} value={groupIds} onChange={setGroupIds} />
      {catalog.data !== undefined && shares.data !== undefined && (
        <GrantEditor
          catalog={catalog.data}
          shares={shares.data}
          value={grants}
          onChange={setEdited}
        />
      )}
      {create.isError && <ErrorNotice error={describeProblem(create.error)} />}
    </FormDialog>
  );
}

function EditDialog({
  app,
  user,
  groups,
  onClose,
}: {
  app: PithyxApp;
  user: UserSummary;
  groups: GroupSummary[];
  onClose: () => void;
}) {
  const { t } = useTranslation('users');
  const [displayName, setDisplayName] = useState(user.displayName ?? '');
  const [groupIds, setGroupIds] = useState<number[]>(user.groupIds);
  const edit = useChange(
    () =>
      usersCall(app, 'internal.admin.users.update', {
        userId: user.id,
        displayName,
        groupIds,
      }),
    onClose,
  );
  return (
    <FormDialog
      size="medium"
      title={t('edit.title', { name: user.username })}
      submit={t('edit.submit')}
      onClose={onClose}
      onSubmit={(event) => {
        event.preventDefault();
        edit.mutate();
      }}
    >
      <TextField
        label={t('field.displayName')}
        value={displayName}
        onChange={setDisplayName}
        autoComplete="off"
      />
      <Memberships groups={groups} value={groupIds} onChange={setGroupIds} />
      {!user.fileServicePassword && <p className={styles.muted}>{t('fileServices.signInOnce')}</p>}
      {edit.isError && <ErrorNotice error={describeProblem(edit.error)} />}
    </FormDialog>
  );
}

/** The groups a user is a member of; the built-in group has everybody and is only named. */
function Memberships({
  groups,
  value,
  onChange,
}: {
  groups: GroupSummary[];
  value: number[];
  onChange: (value: number[]) => void;
}) {
  const { t } = useTranslation('users');
  const own = groups.filter((group) => !group.builtin);
  return (
    <fieldset className={styles.fieldset}>
      <legend className={styles.legend}>{t('field.groups')}</legend>
      {own.map((group) => (
        <Checkbox
          key={group.id}
          isSelected={value.includes(group.id)}
          onChange={(selected) => {
            onChange(selected ? [...value, group.id] : value.filter((id) => id !== group.id));
          }}
        >
          {group.name}
        </Checkbox>
      ))}
      <p className={styles.muted}>{t('field.groupsHint')}</p>
    </fieldset>
  );
}

function PasswordDialog({
  app,
  user,
  onClose,
}: {
  app: PithyxApp;
  user: UserSummary;
  onClose: () => void;
}) {
  const { t } = useTranslation('users');
  const checkPassword = useNewPasswordCheck();
  const [password, setPassword] = useState('');
  const [passwordError, setPasswordError] = useState<string | undefined>();
  const reset = useChange(
    () => usersCall(app, 'internal.admin.users.resetPassword', { userId: user.id, password }),
    onClose,
  );
  return (
    <FormDialog
      title={t('password.title', { name: user.username })}
      submit={t('password.submit')}
      onClose={onClose}
      onSubmit={(event) => {
        event.preventDefault();
        const problem = checkPassword(password);
        setPasswordError(problem);
        if (problem === undefined) {
          reset.mutate();
        }
      }}
    >
      <p className={styles.muted}>{t('password.intro')}</p>
      <p className={styles.muted}>{t('password.fileServices')}</p>
      <NewPasswordField
        label={t('field.newPassword')}
        name="password"
        error={passwordError}
        onChange={(event) => {
          setPassword(event.target.value);
          setPasswordError(undefined);
        }}
        required
      />
      {reset.isError && <ErrorNotice error={describeProblem(reset.error)} />}
    </FormDialog>
  );
}

function DeleteDialog({
  app,
  user,
  onClose,
}: {
  app: PithyxApp;
  user: UserSummary;
  onClose: () => void;
}) {
  const { t } = useTranslation('users');
  const [typed, setTyped] = useState('');
  const [home, setHome] = useState<HomeChoice['home']>('keep');
  const shares = useShares(app);
  const [shareId, setShareId] = useState<number | null>(null);
  const target = shareId ?? shares.data?.[0]?.id ?? null;
  const choice: HomeChoice | null =
    home === 'keep'
      ? { home }
      : home === 'move'
        ? target === null
          ? null
          : { home, shareId: target }
        : { home, confirm: typed };
  const remove = useChange(
    (chosen: HomeChoice) =>
      usersCall(app, 'internal.admin.users.delete', { userId: user.id, ...chosen }),
    onClose,
  );
  return (
    <FormDialog
      title={t('delete.title', { name: user.username })}
      submit={t('delete.submit')}
      danger
      disabled={typed !== user.username || choice === null}
      onClose={onClose}
      onSubmit={(event) => {
        event.preventDefault();
        if (choice !== null) {
          remove.mutate(choice);
        }
      }}
    >
      <p>{t('delete.intro')}</p>
      {user.home != null && (
        <fieldset className={styles.fieldset}>
          <legend className={styles.legend}>{t('delete.home.title')}</legend>
          {(['keep', 'move', 'delete'] as const).map((option) => (
            <label key={option} className={styles.choice}>
              <input
                type="radio"
                name="home"
                checked={home === option}
                onChange={() => {
                  setHome(option);
                }}
              />
              <span>
                {t(`delete.home.${option}`, { name: user.username })}
                <span className={styles.hint}>
                  {t(`delete.home.${option}Hint`, {
                    folder: `${user.username}.deleted-${String(user.uid ?? '')}`,
                  })}
                </span>
              </span>
            </label>
          ))}
          {home === 'move' &&
            (shares.data?.length === 0 ? (
              <p className={styles.muted}>{t('delete.home.noShares')}</p>
            ) : (
              <select
                className={styles.select}
                aria-label={t('delete.home.share')}
                value={target ?? ''}
                onChange={(event) => {
                  setShareId(Number(event.currentTarget.value));
                }}
              >
                {(shares.data ?? []).map((share) => (
                  <option key={share.id} value={share.id}>
                    {share.name}
                  </option>
                ))}
              </select>
            ))}
        </fieldset>
      )}
      <TextField
        label={t('delete.confirm', { name: user.username })}
        value={typed}
        onChange={setTyped}
        autoComplete="off"
        autoCapitalize="none"
      />
      {remove.isError && <ErrorNotice error={describeProblem(remove.error)} />}
    </FormDialog>
  );
}
