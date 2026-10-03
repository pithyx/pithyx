import type { PithyxApp } from '@pithyx/sdk';
import { useMutation, useQuery, useQueryClient, type UseQueryResult } from '@tanstack/react-query';
import { useState, type SyntheticEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { NewPasswordField } from '../../auth/password/NewPasswordField';
import { useNewPasswordCheck } from '../../auth/password/PasswordStrengthMeter';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { Button } from '../../ui/Button';
import { Badge, Checkbox } from '../../ui/Controls';
import { IconPlus } from '../../ui/icons';
import { TextField } from '../../ui/TextField';
import { describeProblem, usersCall, type GroupSummary, type UserSummary } from '../sdk/users';
import { FormDialog } from './FormDialog';
import { GroupsSection, groupsKey, useGroups } from './GroupsSection';
import styles from './Users.module.css';

export const usersKey = ['users'] as const;

type Action =
  | { kind: 'create' }
  | { kind: 'edit'; user: UserSummary }
  | { kind: 'password'; user: UserSummary }
  | { kind: 'delete'; user: UserSummary };

/**
 * The Users page of Settings: every user with its Linux uid and home, its groups, its SMB and SFTP rights and whether
 * those know the password yet, create, edit, reset the password, deactivate, delete; below it the groups.
 */
export function UsersPage({ app }: { app: PithyxApp }) {
  const users = useQuery({
    queryKey: usersKey,
    queryFn: () => usersCall(app, 'internal.admin.users.list', {}),
  });
  return (
    <div className={styles.page}>
      <UsersSection app={app} users={users} />
      <GroupsSection app={app} users={users.data ?? []} />
    </div>
  );
}

function UsersSection({ app, users }: { app: PithyxApp; users: UseQueryResult<UserSummary[]> }) {
  const { t } = useTranslation('users');
  const [action, setAction] = useState<Action | null>(null);
  const groups = useGroups(app);
  const groupNames = new Map((groups.data ?? []).map((group) => [group.id, group.name]));
  const close = () => {
    setAction(null);
  };
  return (
    <section className={styles.section} aria-labelledby="users-title">
      <div className={styles.head}>
        <div>
          <h2 id="users-title" className={styles.title}>
            {t('title')}
          </h2>
          <p className={styles.muted}>{t('intro')}</p>
        </div>
        <Button
          variant="primary"
          onPress={() => {
            setAction({ kind: 'create' });
          }}
        >
          <IconPlus size={16} stroke={1.75} aria-hidden />
          {t('create.button')}
        </Button>
      </div>
      {users.isError && <ErrorNotice error={describeProblem(users.error)} />}
      {users.data !== undefined && (
        <table className={styles.table}>
          <caption className="visually-hidden">{t('title')}</caption>
          <thead>
            <tr>
              <th scope="col">{t('column.name')}</th>
              <th scope="col" className={styles.optional}>
                {t('column.groups')}
              </th>
              <th scope="col" className={styles.optional}>
                {t('column.uid')}
              </th>
              <th scope="col" className={styles.optional}>
                {t('column.home')}
              </th>
              <th scope="col">
                <span className="visually-hidden">{t('column.actions')}</span>
              </th>
            </tr>
          </thead>
          <tbody>
            {users.data.map((user) => (
              <tr key={user.id}>
                <th scope="row">
                  <span className={styles.name}>{user.displayName ?? user.username}</span>
                  {user.displayName !== null && (
                    <span className={styles.muted}> {user.username}</span>
                  )}
                  <span className={styles.badges}>
                    {user.isAdmin && <Badge tone="accent">{t('badge.admin')}</Badge>}
                    {user.totpEnabled && <Badge tone="neutral">{t('badge.totp')}</Badge>}
                    {user.disabled && <Badge tone="neutral">{t('badge.disabled')}</Badge>}
                    {user.smb && <Badge tone="neutral">{t('badge.smb')}</Badge>}
                    {user.sftp && <Badge tone="neutral">{t('badge.sftp')}</Badge>}
                  </span>
                  {!user.disabled && (user.smb || user.sftp) && !user.fileServicePassword && (
                    <span className={styles.hint}>{t('fileServices.signInOnce')}</span>
                  )}
                </th>
                <td className={styles.optional}>
                  {user.groupIds.map((id) => groupNames.get(id) ?? '').join(', ')}
                </td>
                <td className={styles.optional}>{user.uid ?? ''}</td>
                <td className={styles.optional}>{user.home ?? t('noHome')}</td>
                <td>
                  <span className={styles.actions}>
                    <Button
                      size="sm"
                      onPress={() => {
                        setAction({ kind: 'edit', user });
                      }}
                    >
                      {t('edit.button')}
                    </Button>
                    <Button
                      size="sm"
                      onPress={() => {
                        setAction({ kind: 'password', user });
                      }}
                    >
                      {t('password.button')}
                    </Button>
                    <DisableButton app={app} user={user} />
                    <Button
                      size="sm"
                      variant="danger"
                      onPress={() => {
                        setAction({ kind: 'delete', user });
                      }}
                    >
                      {t('delete.button')}
                    </Button>
                  </span>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {action?.kind === 'create' && (
        <CreateDialog app={app} groups={groups.data ?? []} onClose={close} />
      )}
      {action?.kind === 'edit' && (
        <EditDialog app={app} user={action.user} groups={groups.data ?? []} onClose={close} />
      )}
      {action?.kind === 'password' && (
        <PasswordDialog app={app} user={action.user} onClose={close} />
      )}
      {action?.kind === 'delete' && <DeleteDialog app={app} user={action.user} onClose={close} />}
    </section>
  );
}

/** Deactivates or activates a user at once; the daemon refuses it for oneself and the last active administrator. */
function DisableButton({ app, user }: { app: PithyxApp; user: UserSummary }) {
  const { t } = useTranslation('users');
  const client = useQueryClient();
  const toggle = useMutation({
    mutationFn: () =>
      usersCall(app, 'internal.admin.users.update', { userId: user.id, disabled: !user.disabled }),
    onSuccess: async () => {
      await client.invalidateQueries({ queryKey: usersKey });
    },
  });
  return (
    <>
      <Button
        size="sm"
        isDisabled={toggle.isPending}
        aria-label={t(user.disabled ? 'disable.enableLabel' : 'disable.disableLabel', {
          name: user.username,
        })}
        onPress={() => {
          toggle.mutate();
        }}
      >
        {user.disabled ? t('disable.enable') : t('disable.disable')}
      </Button>
      {toggle.isError && <ErrorNotice error={describeProblem(toggle.error)} />}
    </>
  );
}

function useChange<T>(run: () => Promise<T>, onClose: () => void) {
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
  const [isAdmin, setIsAdmin] = useState(false);
  const [protocols, setProtocols] = useState({ smb: true, sftp: false });
  const [groupIds, setGroupIds] = useState<number[]>([]);
  const [passwordError, setPasswordError] = useState<string | undefined>();
  const create = useChange(
    () =>
      usersCall(app, 'internal.admin.users.create', {
        username,
        password,
        displayName: displayName.trim() === '' ? null : displayName,
        isAdmin,
        ...protocols,
        groupIds,
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
      <Checkbox isSelected={isAdmin} onChange={setIsAdmin}>
        {t('field.admin')}
      </Checkbox>
      <Protocols isAdmin={isAdmin} value={protocols} onChange={setProtocols} />
      <Memberships groups={groups} value={groupIds} onChange={setGroupIds} />
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
  const [isAdmin, setIsAdmin] = useState(user.isAdmin);
  const [protocols, setProtocols] = useState({ smb: user.smb, sftp: user.sftp });
  const [groupIds, setGroupIds] = useState<number[]>(user.groupIds);
  const edit = useChange(
    () =>
      usersCall(app, 'internal.admin.users.update', {
        userId: user.id,
        displayName,
        isAdmin,
        ...protocols,
        groupIds,
      }),
    onClose,
  );
  return (
    <FormDialog
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
      <Checkbox isSelected={isAdmin} onChange={setIsAdmin}>
        {t('field.admin')}
      </Checkbox>
      <Protocols isAdmin={isAdmin} value={protocols} onChange={setProtocols} />
      <Memberships groups={groups} value={groupIds} onChange={setGroupIds} />
      {!user.fileServicePassword && <p className={styles.muted}>{t('fileServices.signInOnce')}</p>}
      {edit.isError && <ErrorNotice error={describeProblem(edit.error)} />}
    </FormDialog>
  );
}

interface ProtocolRights {
  smb: boolean;
  sftp: boolean;
}

/** The SMB and SFTP rights of a user; administrators have both, whatever is stored. */
function Protocols({
  isAdmin,
  value,
  onChange,
}: {
  isAdmin: boolean;
  value: ProtocolRights;
  onChange: (value: ProtocolRights) => void;
}) {
  const { t } = useTranslation('users');
  return (
    <fieldset className={styles.fieldset}>
      <legend className={styles.legend}>{t('fileServices.title')}</legend>
      <Checkbox
        isSelected={isAdmin || value.smb}
        isDisabled={isAdmin}
        onChange={(smb) => {
          onChange({ ...value, smb });
        }}
      >
        {t('fileServices.smb')}
      </Checkbox>
      <Checkbox
        isSelected={isAdmin || value.sftp}
        isDisabled={isAdmin}
        onChange={(sftp) => {
          onChange({ ...value, sftp });
        }}
      >
        {t('fileServices.sftp')}
      </Checkbox>
      {isAdmin && <p className={styles.muted}>{t('fileServices.admin')}</p>}
    </fieldset>
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
  const remove = useChange(
    () => usersCall(app, 'internal.admin.users.delete', { userId: user.id }),
    onClose,
  );
  return (
    <FormDialog
      title={t('delete.title', { name: user.username })}
      submit={t('delete.submit')}
      danger
      disabled={typed !== user.username}
      onClose={onClose}
      onSubmit={(event) => {
        event.preventDefault();
        remove.mutate();
      }}
    >
      <p>{t('delete.intro')}</p>
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
