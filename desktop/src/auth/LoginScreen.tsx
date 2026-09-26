import { IconArrowLeft } from '@tabler/icons-react';
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
import { cx } from '../ui/cx';
import { OtpField, PasswordField, TextField } from '../ui/TextField';
import styles from './Auth.module.css';
import { initialsOf, LanguageMenu } from './parts';
import { RecoveryKeyCard } from './RecoveryKeyCard';
import { MIN_PASSWORD_LENGTH } from './SetupScreen';

type User = components['schemas']['UserInfo'];

type Step =
  | { kind: 'password' }
  | { kind: 'totp'; challenge: string }
  | { kind: 'forgot' }
  | { kind: 'recovered'; recoveryKey: string };

/**
 * Sign-in in one card: password, then the TOTP step for users who turned it on. "Forgot password?" explains that
 * users ask an administrator and lets the administrator reset the password with the recovery key.
 */
export function LoginScreen({
  hostname,
  onSignedIn,
}: {
  hostname: string;
  onSignedIn: (user: User) => void;
}) {
  const { t } = useTranslation();
  const [step, setStep] = useState<Step>({ kind: 'password' });
  // Kept across steps, so going back or finishing a reset does not ask for the name again.
  const [username, setUsername] = useState('');
  const toPassword = () => {
    setStep({ kind: 'password' });
  };

  let content;
  switch (step.kind) {
    case 'password':
      content = (
        <PasswordStep
          username={username}
          onUsername={setUsername}
          onTotp={(challenge) => {
            setStep({ kind: 'totp', challenge });
          }}
          onSignedIn={onSignedIn}
          onForgot={() => {
            setStep({ kind: 'forgot' });
          }}
        />
      );
      break;
    case 'totp':
      content = (
        <TotpStep
          username={username}
          challenge={step.challenge}
          onBack={toPassword}
          onSignedIn={onSignedIn}
        />
      );
      break;
    case 'forgot':
      content = (
        <ForgotStep
          username={username}
          onUsername={setUsername}
          onBack={toPassword}
          onRecovered={(recoveryKey) => {
            setStep({ kind: 'recovered', recoveryKey });
          }}
        />
      );
      break;
    case 'recovered':
      content = <RecoveredStep recoveryKey={step.recoveryKey} onContinue={toPassword} />;
      break;
  }

  return (
    <AuthLayout wide={step.kind !== 'password' && step.kind !== 'totp'} footer={<LanguageMenu />}>
      {(step.kind === 'password' || step.kind === 'recovered') && (
        <AuthHeader host={hostname} product={t('brand')} />
      )}
      {content}
    </AuthLayout>
  );
}

function PasswordStep({
  username,
  onUsername,
  onTotp,
  onSignedIn,
  onForgot,
}: {
  username: string;
  onUsername: (value: string) => void;
  onTotp: (challenge: string) => void;
  onSignedIn: (user: User) => void;
  onForgot: () => void;
}) {
  const { t } = useTranslation('auth');
  const login = useMutation({
    mutationFn: async (body: { username: string; password: string }) =>
      unwrap(await api.POST('/api/auth/login', { body })),
    onSuccess: (response) => {
      if (response.totpRequired && response.challenge !== null) {
        onTotp(response.challenge);
      } else if (response.user !== null) {
        onSignedIn(response.user);
      }
    },
  });

  function submit(event: SyntheticEvent<HTMLFormElement>) {
    event.preventDefault();
    login.mutate({ username, password: field(new FormData(event.currentTarget), 'password') });
  }

  return (
    <Form
      className={styles.form}
      onSubmit={submit}
      validationBehavior="aria"
      aria-labelledby="login-title"
    >
      <h1 id="login-title" className={styles.title}>
        {t('login.heading')}
      </h1>
      <TextField
        label={t('login.username')}
        name="username"
        autoComplete="username"
        autoCapitalize="none"
        spellCheck="false"
        value={username}
        onChange={onUsername}
        autoFocus={username === ''}
        isRequired
      />
      <PasswordField
        label={t('login.password')}
        name="password"
        autoComplete="current-password"
        autoFocus={username !== ''}
        isRequired
      />
      {login.isError && <ErrorNotice error={describeError(login.error)} />}
      <Button type="submit" variant="primary" className={styles.submit} isPending={login.isPending}>
        {t('login.submit')}
      </Button>
      <div className={styles.links}>
        <Button variant="quiet" size="sm" onPress={onForgot}>
          {t('login.forgot')}
        </Button>
      </div>
    </Form>
  );
}

function TotpStep({
  username,
  challenge,
  onBack,
  onSignedIn,
}: {
  username: string;
  challenge: string;
  onBack: () => void;
  onSignedIn: (user: User) => void;
}) {
  const { t } = useTranslation('auth');
  const [useRecoveryCode, setUseRecoveryCode] = useState(false);
  const verify = useMutation({
    mutationFn: async (body: {
      challenge: string;
      code: string | null;
      recoveryCode: string | null;
    }) => unwrap(await api.POST('/api/auth/login/totp', { body })),
    onSuccess: onSignedIn,
  });

  function submit(event: SyntheticEvent<HTMLFormElement>) {
    event.preventDefault();
    const value = field(new FormData(event.currentTarget), 'code').trim();
    verify.mutate({
      challenge,
      code: useRecoveryCode ? null : value,
      recoveryCode: useRecoveryCode ? value : null,
    });
  }

  return (
    <Form
      className={styles.form}
      onSubmit={submit}
      validationBehavior="aria"
      aria-labelledby="totp-title"
    >
      <div className={styles.userRow}>
        <Button variant="quiet" size="sm" onPress={onBack} aria-label={t('totp.back')}>
          <IconArrowLeft size={18} stroke={1.75} aria-hidden />
        </Button>
        <span className={styles.avatar} aria-hidden>
          {initialsOf(username)}
        </span>
        <span className={styles.userName}>{username}</span>
      </div>
      <h1 id="totp-title" className={styles.title}>
        {t('totp.title')}
      </h1>
      {useRecoveryCode ? (
        <TextField
          key="recovery"
          label={t('totp.recoveryCode')}
          name="code"
          autoComplete="off"
          autoCapitalize="characters"
          spellCheck="false"
          inputClassName={styles.mono}
          autoFocus
          isRequired
        />
      ) : (
        <OtpField key="code" label={t('totp.code')} name="code" autoFocus isRequired />
      )}
      {verify.isError && <ErrorNotice error={describeError(verify.error)} />}
      <Button
        type="submit"
        variant="primary"
        className={styles.submit}
        isPending={verify.isPending}
      >
        {t('totp.submit')}
      </Button>
      <div className={styles.links}>
        <Button
          variant="quiet"
          size="sm"
          onPress={() => {
            setUseRecoveryCode(!useRecoveryCode);
            verify.reset();
          }}
        >
          {useRecoveryCode ? t('totp.useCode') : t('totp.useRecoveryCode')}
        </Button>
      </div>
    </Form>
  );
}

/** No reset by email: users ask an administrator, administrators use the recovery key. */
function ForgotStep({
  username,
  onUsername,
  onBack,
  onRecovered,
}: {
  username: string;
  onUsername: (value: string) => void;
  onBack: () => void;
  onRecovered: (recoveryKey: string) => void;
}) {
  const { t } = useTranslation('auth');
  const recover = useMutation({
    mutationFn: async (body: { username: string; recoveryKey: string; newPassword: string }) =>
      unwrap(await api.POST('/api/auth/recover', { body })),
    onSuccess: (response) => {
      onRecovered(response.recoveryKey);
    },
  });

  function submit(event: SyntheticEvent<HTMLFormElement>) {
    event.preventDefault();
    const form = new FormData(event.currentTarget);
    recover.mutate({
      username,
      recoveryKey: field(form, 'recoveryKey'),
      newPassword: field(form, 'newPassword'),
    });
  }

  return (
    <div className={styles.form}>
      <div className={styles.heading}>
        <h1 id="forgot-title" className={styles.title}>
          {t('forgot.title')}
        </h1>
        <p className={styles.intro}>{t('forgot.intro')}</p>
      </div>
      <Form
        className={cx(styles.form, styles.adminBox)}
        onSubmit={submit}
        validationBehavior="aria"
        aria-labelledby="forgot-admin"
      >
        <div className={styles.heading}>
          <h2 id="forgot-admin" className={styles.subtitle}>
            {t('forgot.adminTitle')}
          </h2>
          <p className={styles.intro}>{t('forgot.adminIntro')}</p>
        </div>
        <TextField
          label={t('login.username')}
          name="username"
          autoComplete="username"
          autoCapitalize="none"
          spellCheck="false"
          value={username}
          onChange={onUsername}
          autoFocus
          isRequired
        />
        <TextField
          label={t('forgot.recoveryKey')}
          name="recoveryKey"
          autoComplete="off"
          autoCapitalize="characters"
          spellCheck="false"
          inputClassName={styles.mono}
          isRequired
        />
        <PasswordField
          label={t('forgot.newPassword')}
          description={t('setup.passwordHint', { minLength: MIN_PASSWORD_LENGTH })}
          name="newPassword"
          autoComplete="new-password"
          isRequired
        />
        {recover.isError && <ErrorNotice error={describeError(recover.error)} />}
        <Button
          type="submit"
          variant="primary"
          className={styles.submit}
          isPending={recover.isPending}
        >
          {t('forgot.submit')}
        </Button>
      </Form>
      <div className={styles.links}>
        <Button variant="quiet" size="sm" onPress={onBack}>
          <IconArrowLeft size={16} stroke={1.75} aria-hidden />
          {t('forgot.back')}
        </Button>
      </div>
    </div>
  );
}

/** The reset replaced the recovery key; the new one is shown once, like during the setup. */
function RecoveredStep({
  recoveryKey,
  onContinue,
}: {
  recoveryKey: string;
  onContinue: () => void;
}) {
  const { t } = useTranslation('auth');
  return (
    <RecoveryKeyCard
      recoveryKey={recoveryKey}
      title={t('forgot.doneTitle')}
      intro={t('forgot.doneIntro')}
      continueLabel={t('forgot.doneContinue')}
      onContinue={onContinue}
    />
  );
}
