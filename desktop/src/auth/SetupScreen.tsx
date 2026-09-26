import { useMutation } from '@tanstack/react-query';
import { unwrap, type components } from '@vyxis/api-client';
import { useState, type SyntheticEvent } from 'react';
import { Form } from 'react-aria-components';
import { useTranslation } from 'react-i18next';
import { api } from '../api/client';
import { describeError } from '../errors/describe';
import { ErrorNotice } from '../errors/ErrorNotice';
import { field } from '../forms';
import { AuthHeader, AuthLayout } from '../shell/AuthLayout';
import { Button } from '../ui/Button';
import { PasswordField, TextField } from '../ui/TextField';
import styles from './Auth.module.css';
import { LanguageMenu } from './parts';
import { RecoveryKeyCard } from './RecoveryKeyCard';

type User = components['schemas']['UserInfo'];

export const MIN_PASSWORD_LENGTH = 12;
const STEPS = 2;

/**
 * First run: create the administrator, then store the recovery key. The setup signs the administrator in.
 *
 * Auth forms validate only on submit (validationBehavior "aria" plus errors set by the submit handler) and leave
 * the rules to the daemon. React Aria's native mode shows errors on blur, and an error appearing or vanishing while
 * the pointer is on the submit button moves the button away before the click completes.
 */
export function SetupScreen({
  hostname,
  onSignedIn,
}: {
  hostname: string;
  onSignedIn: (user: User) => void;
}) {
  const { t } = useTranslation('auth');
  const { t: tDesktop } = useTranslation();
  const [mismatch, setMismatch] = useState(false);
  const setup = useMutation({
    mutationFn: async (body: { username: string; password: string; displayName: string | null }) =>
      unwrap(await api.POST('/api/setup', { body })),
  });

  function submit(event: SyntheticEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = new FormData(event.currentTarget);
    const password = field(form, 'password');
    if (password !== field(form, 'confirm')) {
      setMismatch(true);
      return;
    }
    const displayName = field(form, 'displayName').trim();
    setup.mutate({
      username: field(form, 'username'),
      password,
      displayName: displayName === '' ? null : displayName,
    });
  }

  return (
    <AuthLayout wide footer={<LanguageMenu />}>
      <AuthHeader host={hostname} product={tDesktop('brand')} />
      {setup.data === undefined ? (
        <Form
          className={styles.form}
          onSubmit={submit}
          validationBehavior="aria"
          aria-labelledby="setup-title"
        >
          <div className={styles.heading}>
            <p className={styles.step}>{t('setup.step', { current: 1, total: STEPS })}</p>
            <h1 id="setup-title" className={styles.title}>
              {t('setup.title')}
            </h1>
            <p className={styles.intro}>{t('setup.intro')}</p>
          </div>
          <TextField
            label={t('setup.username')}
            description={t('setup.usernameHint')}
            name="username"
            autoComplete="username"
            autoCapitalize="none"
            spellCheck="false"
            autoFocus
            isRequired
          />
          <TextField label={t('setup.displayName')} name="displayName" autoComplete="name" />
          <PasswordField
            label={t('setup.password')}
            description={t('setup.passwordHint', { minLength: MIN_PASSWORD_LENGTH })}
            name="password"
            autoComplete="new-password"
            onChange={() => {
              setMismatch(false);
            }}
            isRequired
          />
          <PasswordField
            label={t('setup.confirmPassword')}
            name="confirm"
            autoComplete="new-password"
            isInvalid={mismatch}
            errorMessage={t('setup.passwordMismatch')}
            onChange={() => {
              setMismatch(false);
            }}
            isRequired
          />
          {setup.isError && <ErrorNotice error={describeError(setup.error)} />}
          <Button
            type="submit"
            variant="primary"
            className={styles.submit}
            isPending={setup.isPending}
          >
            {t('setup.submit')}
          </Button>
        </Form>
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
