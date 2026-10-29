import type { PithyxApp } from '@pithyx/sdk';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState, type SyntheticEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { NewPasswordField } from '../../auth/password/NewPasswordField';
import { useNewPasswordCheck } from '../../auth/password/PasswordStrengthMeter';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { Badge, Button, Checkbox, TextField } from '@pithyx/ui';
import { IconPlus } from '../../ui/icons';
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
import { GrantEditor, protocolKey } from './GrantEditor';
import { groupsKey, useGroups, usersKey, useUsers } from './queries';
import { RightsDialog, useGrantChoices } from './RightsDialog';
import styles from './Users.module.css';

type Action =
  | { kind: 'create' }
  | { kind: 'edit'; user: UserSummary }
  | { kind: 'rights'; user: UserSummary }
  | { kind: 'password'; user: UserSummary }
  | { kind: 'delete'; user: UserSummary };

/**
 * The Users page of Settings: every user with its Linux uid and home, its groups, its file protocols and whether they
 * know the password yet; create, edit, rights, new password, deactivate, delete. Groups have their own page.
 */
export function UsersPage({ app }: { app: PithyxApp }) {
  const { t } = useTranslation('users');
  const users = useUsers(app);
  const [action, setAction] = useState<Action | null>(null);
  const groups = useGroups(app);
  const groupNames = new Map((groups.data ?? []).map((group) => [group.id, group.name]));
  const close = () => {
    setAction(null);
  };
  return (
    <section className={`${styles.page} ${styles.section}`}>
      <div className={styles.head}>
        <div>
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
                    {user.protocols.map((protocol) => (
                      <Badge key={protocol} tone="neutral">
                        {t(`grants.protocol.${protocolKey(protocol)}`)}
                      </Badge>
                    ))}
                  </span>
                  {!user.disabled && user.protocols.length > 0 && !user.fileServicePassword && (
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
                        setAction({ kind: 'rights', user });
                      }}
                    >
                      {t('grants.button')}
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
      {action?.kind === 'rights' && (
        <RightsDialog app={app} subject={{ kind: 'user', user: action.user }} onClose={close} />
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
