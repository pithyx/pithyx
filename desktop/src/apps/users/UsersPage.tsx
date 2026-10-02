import type { PithyxApp } from '@pithyx/sdk';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useId, useState, type ReactNode, type SyntheticEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { NewPasswordField } from '../../auth/password/NewPasswordField';
import { useNewPasswordCheck } from '../../auth/password/PasswordStrengthMeter';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { Button } from '../../ui/Button';
import { Badge, Checkbox } from '../../ui/Controls';
import { Dialog } from '../../ui/Dialog';
import { IconPlus } from '../../ui/icons';
import { TextField } from '../../ui/TextField';
import { describeProblem, usersCall, type UserSummary } from '../sdk/users';
import styles from './Users.module.css';

const usersKey = ['users'] as const;

type Action =
  | { kind: 'create' }
  | { kind: 'edit'; user: UserSummary }
  | { kind: 'password'; user: UserSummary }
  | { kind: 'delete'; user: UserSummary };

/**
 * The Users page: every user with its Linux uid and home, create, edit, reset the password, deactivate, delete. A standalone
 * component; the Settings app registers it once its new shell is in dev (with `usersHost` as internal methods).
 */
export function UsersPage({ app }: { app: PithyxApp }) {
  const { t } = useTranslation('users');
  const [action, setAction] = useState<Action | null>(null);
  const users = useQuery({
    queryKey: usersKey,
    queryFn: () => usersCall(app, 'internal.admin.users.list', {}),
  });
  const close = () => {
    setAction(null);
  };
  return (
    <section className={styles.page} aria-labelledby="users-title">
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
                  </span>
                </th>
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
      {action?.kind === 'create' && <CreateDialog app={app} onClose={close} />}
      {action?.kind === 'edit' && <EditDialog app={app} user={action.user} onClose={close} />}
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
      await client.invalidateQueries({ queryKey: usersKey });
      onClose();
    },
  });
}

function CreateDialog({ app, onClose }: { app: PithyxApp; onClose: () => void }) {
  const { t } = useTranslation('users');
  const checkPassword = useNewPasswordCheck();
  const [username, setUsername] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [password, setPassword] = useState('');
  const [isAdmin, setIsAdmin] = useState(false);
  const [passwordError, setPasswordError] = useState<string | undefined>();
  const create = useChange(
    () =>
      usersCall(app, 'internal.admin.users.create', {
        username,
        password,
        displayName: displayName.trim() === '' ? null : displayName,
        isAdmin,
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
      {create.isError && <ErrorNotice error={describeProblem(create.error)} />}
    </FormDialog>
  );
}

function EditDialog({
  app,
  user,
  onClose,
}: {
  app: PithyxApp;
  user: UserSummary;
  onClose: () => void;
}) {
  const { t } = useTranslation('users');
  const [displayName, setDisplayName] = useState(user.displayName ?? '');
  const [isAdmin, setIsAdmin] = useState(user.isAdmin);
  const edit = useChange(
    () => usersCall(app, 'internal.admin.users.update', { userId: user.id, displayName, isAdmin }),
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
      {edit.isError && <ErrorNotice error={describeProblem(edit.error)} />}
    </FormDialog>
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

function FormDialog({
  title,
  submit,
  danger = false,
  disabled = false,
  onClose,
  onSubmit,
  children,
}: {
  title: string;
  submit: string;
  danger?: boolean;
  disabled?: boolean;
  onClose: () => void;
  onSubmit: (event: SyntheticEvent<HTMLFormElement>) => void;
  children: ReactNode;
}) {
  const { t } = useTranslation('users');
  const formId = useId();
  return (
    <Dialog
      isOpen
      isDismissable
      tone={danger ? 'danger' : 'default'}
      onOpenChange={(open) => {
        if (!open) {
          onClose();
        }
      }}
      title={title}
      actions={
        <>
          <Button onPress={onClose}>{t('cancel')}</Button>
          <Button
            type="submit"
            form={formId}
            variant={danger ? 'danger' : 'primary'}
            isDisabled={disabled}
          >
            {submit}
          </Button>
        </>
      }
    >
      <form id={formId} className={styles.form} onSubmit={onSubmit} noValidate>
        {children}
      </form>
    </Dialog>
  );
}
