import { useMutation } from '@tanstack/react-query';
import { unwrap, type components } from '@pithyx/api-client';
import type { SyntheticEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { api } from '../api/client';
import { describeError } from '../errors/describe';
import { ErrorNotice } from '../errors/ErrorNotice';
import { field } from '../forms';
import { AuthHeader, AuthLayout } from '../shell/AuthLayout';
import styles from './Auth.module.css';
import { Button, Field, LanguageSelect, PasswordField } from './controls';
import { useFormErrors } from './formErrors';
import { RecoveryKeyCard } from './RecoveryKeyCard';

type User = components['schemas']['UserInfo'];

export const MIN_PASSWORD_LENGTH = 12;
const STEPS = 2;

type SetupField = 'username' | 'displayName' | 'password' | 'confirm';
const FIELD_ORDER: readonly SetupField[] = ['username', 'displayName', 'password', 'confirm'];

/** Which field a daemon error belongs to; the rest is shown below the form with its request id. */
function fieldOf(code: string): SetupField | undefined {
  if (code.startsWith('auth.username.')) {
    return 'username';
  }
  if (code.startsWith('auth.display_name.')) {
    return 'displayName';
  }
  return code.startsWith('auth.password.') ? 'password' : undefined;
}

/**
 * First run: create the administrator, then store the recovery key. The setup signs the administrator in.
 *
 * The form checks only what the daemon cannot (both passwords match, nothing left empty) and leaves every rule to
 * the daemon. Errors are only shown after submitting, so nothing moves under the pointer while clicking.
 */
export function SetupScreen({
  hostname,
  onSignedIn,
}: {
  hostname: string;
  onSignedIn: (user: User) => void;
}) {
  const { t } = useTranslation('auth');
  const form = useFormErrors<SetupField>(FIELD_ORDER);
  const setup = useMutation({
    mutationFn: async (body: { username: string; password: string; displayName: string | null }) =>
      unwrap(await api.POST('/api/setup', { body })),
    onError: (error) => {
      const described = describeError(error);
      const target = fieldOf(described.code);
      form.fail(target === undefined ? {} : { [target]: described }, target ?? 'username');
    },
  });
  const noticeError =
    setup.isError && fieldOf(describeError(setup.error).code) === undefined
      ? describeError(setup.error)
      : undefined;

  function submit(event: SyntheticEvent<HTMLFormElement>) {
    event.preventDefault();
    if (setup.isPending) {
      return;
    }
    const data = new FormData(event.currentTarget);
    const username = field(data, 'username').trim();
    const password = field(data, 'password');
    const errors: Partial<Record<SetupField, string>> = {};
    if (username === '') {
      errors.username = t('validation.usernameRequired');
    }
    if (password === '') {
      errors.password = t('validation.passwordRequired');
    } else if (password !== field(data, 'confirm')) {
      errors.confirm = t('setup.passwordMismatch');
    }
    if (form.check(errors)) {
      const displayName = field(data, 'displayName').trim();
      setup.mutate({ username, password, displayName: displayName === '' ? null : displayName });
    }
  }

  return (
    <AuthLayout wide footer={<LanguageSelect />}>
      <AuthHeader host={hostname} motion="loop" />
      {setup.data === undefined ? (
        <form className={styles.form} onSubmit={submit} noValidate aria-labelledby="setup-title">
          <div className={styles.heading}>
            <p className={styles.step}>{t('setup.step', { current: 1, total: STEPS })}</p>
            <h1 id="setup-title" className={styles.title}>
              {t('setup.title')}
            </h1>
            <p className={styles.intro}>{t('setup.intro')}</p>
          </div>
          <Field
            {...form.register('username')}
            label={t('setup.username')}
            description={t('setup.usernameHint')}
            name="username"
            autoComplete="username"
            autoCapitalize="none"
            spellCheck={false}
            autoFocus
            required
          />
          <Field
            {...form.register('displayName')}
            label={t('setup.displayName')}
            name="displayName"
            autoComplete="name"
          />
          <PasswordField
            {...form.register('password')}
            label={t('setup.password')}
            description={t('setup.passwordHint', { minLength: MIN_PASSWORD_LENGTH })}
            name="password"
            autoComplete="new-password"
            required
          />
          <PasswordField
            {...form.register('confirm')}
            label={t('setup.confirmPassword')}
            name="confirm"
            autoComplete="new-password"
            required
          />
          {noticeError !== undefined && <ErrorNotice error={noticeError} />}
          {form.announcement}
          <Button
            type="submit"
            variant="primary"
            className={styles.submit}
            pending={setup.isPending}
          >
            {setup.isPending ? t('setup.submitting') : t('setup.submit')}
          </Button>
        </form>
      ) : (
        <RecoveryKeyCard
          recoveryKey={setup.data.recoveryKey}
          step={t('setup.step', { current: 2, total: STEPS })}
          title={t('recoveryKey.title')}
          intro={t('recoveryKey.intro')}
          continueLabel={t('recoveryKey.continue')}
          onContinue={() => {
            onSignedIn(setup.data.user);
          }}
        />
      )}
    </AuthLayout>
  );
}
