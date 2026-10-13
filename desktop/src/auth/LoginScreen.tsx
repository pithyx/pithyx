import { useMutation } from '@tanstack/react-query';
import { unwrap, type components } from '@pithyx/api-client';
import { useState, type SyntheticEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { api } from '../api/client';
import { describeError } from '../errors/describe';
import { ErrorNotice } from '../errors/ErrorNotice';
import { field } from '../forms';
import { AuthHeader, AuthLayout } from '../shell/AuthLayout';
import { cx, textFieldStyles } from '@pithyx/ui/native';
import { IconArrowLeft } from '../ui/icons';
import styles from './Auth.module.css';
import { Button, Field, LanguageSelect, PasswordField } from './controls';
import { useFormErrors } from './formErrors';
import { initialsOf } from './parts';
import { NewPasswordField } from './password/NewPasswordField';
import { useNewPasswordCheck } from './password/PasswordStrengthMeter';
import { RecoveryKeyCard } from './RecoveryKeyCard';

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
  continueTo,
  onSignedIn,
}: {
  hostname: string;
  /** The proxy host the browser goes on to after signing in, if any. */
  continueTo?: string | undefined;
  onSignedIn: (user: User) => void;
}) {
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
          continueTo={continueTo}
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
    <AuthLayout wide={step.kind !== 'password' && step.kind !== 'totp'} footer={<LanguageSelect />}>
      {(step.kind === 'password' || step.kind === 'recovered') && (
        <AuthHeader host={hostname} motion="closed" />
      )}
      {content}
    </AuthLayout>
  );
}

type PasswordFieldName = 'username' | 'password';

function PasswordStep({
  continueTo,
  username,
  onUsername,
  onTotp,
  onSignedIn,
  onForgot,
}: {
  continueTo: string | undefined;
  username: string;
  onUsername: (value: string) => void;
  onTotp: (challenge: string) => void;
  onSignedIn: (user: User) => void;
  onForgot: () => void;
}) {
  const { t } = useTranslation('auth');
  const form = useFormErrors<PasswordFieldName>(['username', 'password']);
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
    // Wrong credentials: the notice explains, the password is selected for retyping.
    onError: () => {
      form.fail({}, 'password');
    },
  });

  function submit(event: SyntheticEvent<HTMLFormElement>) {
    event.preventDefault();
    if (login.isPending) {
      return;
    }
    const password = field(new FormData(event.currentTarget), 'password');
    const errors: Partial<Record<PasswordFieldName, string>> = {};
    if (username.trim() === '') {
      errors.username = t('validation.usernameRequired');
    }
    if (password === '') {
      errors.password = t('validation.passwordRequired');
    }
    if (form.check(errors)) {
      login.mutate({ username: username.trim(), password });
    }
  }

  const usernameField = form.register('username');
  return (
    <form className={styles.form} onSubmit={submit} noValidate aria-labelledby="login-title">
      <h1 id="login-title" className={styles.title}>
        {t('login.heading')}
      </h1>
      {continueTo !== undefined && (
        <p className={styles.intro}>{t('login.continueTo', { host: continueTo })}</p>
      )}
      <Field
        {...usernameField}
        label={t('login.username')}
        name="username"
        autoComplete="username"
        autoCapitalize="none"
        spellCheck={false}
        value={username}
        onChange={(event) => {
          usernameField.onChange();
          onUsername(event.target.value);
        }}
        autoFocus={username === ''}
        required
      />
      <PasswordField
        {...form.register('password')}
        label={t('login.password')}
        name="password"
        autoComplete="current-password"
        autoFocus={username !== ''}
        required
      />
      {login.isError && <ErrorNotice error={describeError(login.error)} />}
      {form.announcement}
      <Button type="submit" variant="primary" className={styles.submit} pending={login.isPending}>
        {login.isPending ? t('login.submitting') : t('login.submit')}
      </Button>
      <div className={styles.links}>
        <Button variant="quiet" size="sm" onClick={onForgot}>
          {t('login.forgot')}
        </Button>
      </div>
    </form>
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
  const form = useFormErrors<'code'>(['code']);
  const verify = useMutation({
    mutationFn: async (body: {
      challenge: string;
      code: string | null;
      recoveryCode: string | null;
    }) => unwrap(await api.POST('/api/auth/login/totp', { body })),
    onSuccess: onSignedIn,
    onError: () => {
      form.fail({}, 'code');
    },
  });

  function submit(event: SyntheticEvent<HTMLFormElement>) {
    event.preventDefault();
    if (verify.isPending) {
      return;
    }
    const value = field(new FormData(event.currentTarget), 'code').trim();
    if (form.check(value === '' ? { code: t('validation.codeRequired') } : {})) {
      verify.mutate({
        challenge,
        code: useRecoveryCode ? null : value,
        recoveryCode: useRecoveryCode ? value : null,
      });
    }
  }

  return (
    <form className={styles.form} onSubmit={submit} noValidate aria-labelledby="totp-title">
      <div className={styles.userRow}>
        <Button variant="quiet" size="sm" onClick={onBack} aria-label={t('totp.back')}>
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
        <Field
          {...form.register('code')}
          key="recovery"
          label={t('totp.recoveryCode')}
          name="code"
          autoComplete="off"
          autoCapitalize="characters"
          spellCheck={false}
          inputClassName={styles.mono}
          autoFocus
          required
        />
      ) : (
        <Field
          {...form.register('code')}
          key="code"
          label={t('totp.code')}
          name="code"
          inputMode="numeric"
          autoComplete="one-time-code"
          maxLength={7}
          inputClassName={textFieldStyles.otp}
          autoFocus
          required
        />
      )}
      {verify.isError && <ErrorNotice error={describeError(verify.error)} />}
      {form.announcement}
      <Button type="submit" variant="primary" className={styles.submit} pending={verify.isPending}>
        {verify.isPending ? t('totp.submitting') : t('totp.submit')}
      </Button>
      <div className={styles.links}>
        <Button
          variant="quiet"
          size="sm"
          onClick={() => {
            setUseRecoveryCode(!useRecoveryCode);
            verify.reset();
          }}
        >
          {useRecoveryCode ? t('totp.useCode') : t('totp.useRecoveryCode')}
        </Button>
      </div>
    </form>
  );
}

type ForgotFieldName = 'username' | 'recoveryKey' | 'newPassword';

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
  const form = useFormErrors<ForgotFieldName>(['username', 'recoveryKey', 'newPassword']);
  const checkPassword = useNewPasswordCheck();
  const recover = useMutation({
    mutationFn: async (body: { username: string; recoveryKey: string; newPassword: string }) =>
      unwrap(await api.POST('/api/auth/recover', { body })),
    onSuccess: (response) => {
      onRecovered(response.recoveryKey);
    },
    onError: (error) => {
      const described = describeError(error);
      if (described.code.startsWith('auth.password.')) {
        form.fail({ newPassword: described }, 'newPassword');
      } else {
        form.fail({}, 'recoveryKey');
      }
    },
  });
  const noticeError =
    recover.isError && !describeError(recover.error).code.startsWith('auth.password.')
      ? describeError(recover.error)
      : undefined;

  function submit(event: SyntheticEvent<HTMLFormElement>) {
    event.preventDefault();
    if (recover.isPending) {
      return;
    }
    const data = new FormData(event.currentTarget);
    const recoveryKey = field(data, 'recoveryKey').trim();
    const newPassword = field(data, 'newPassword');
    const errors: Partial<Record<ForgotFieldName, string>> = {};
    if (username.trim() === '') {
      errors.username = t('validation.usernameRequired');
    }
    if (recoveryKey === '') {
      errors.recoveryKey = t('validation.recoveryKeyRequired');
    }
    const passwordError = checkPassword(newPassword);
    if (newPassword === '') {
      errors.newPassword = t('validation.passwordRequired');
    } else if (passwordError !== undefined) {
      errors.newPassword = passwordError;
    }
    if (form.check(errors)) {
      recover.mutate({ username: username.trim(), recoveryKey, newPassword });
    }
  }

  const usernameField = form.register('username');
  return (
    <div className={styles.form}>
      <div className={styles.heading}>
        <h1 id="forgot-title" className={styles.title}>
          {t('forgot.title')}
        </h1>
        <p className={styles.intro}>{t('forgot.intro')}</p>
      </div>
      <form
        className={cx(styles.form, styles.adminBox)}
        onSubmit={submit}
        noValidate
        aria-labelledby="forgot-admin"
      >
        <div className={styles.heading}>
          <h2 id="forgot-admin" className={styles.subtitle}>
            {t('forgot.adminTitle')}
          </h2>
          <p className={styles.intro}>{t('forgot.adminIntro')}</p>
        </div>
        <Field
          {...usernameField}
          label={t('login.username')}
          name="username"
          autoComplete="username"
          autoCapitalize="none"
          spellCheck={false}
          value={username}
          onChange={(event) => {
            usernameField.onChange();
            onUsername(event.target.value);
          }}
          autoFocus
          required
        />
        <Field
          {...form.register('recoveryKey')}
          label={t('forgot.recoveryKey')}
          name="recoveryKey"
          autoComplete="off"
          autoCapitalize="characters"
          spellCheck={false}
          inputClassName={styles.mono}
          required
        />
        <NewPasswordField
          {...form.register('newPassword')}
          label={t('forgot.newPassword')}
          name="newPassword"
          required
        />
        {noticeError !== undefined && <ErrorNotice error={noticeError} />}
        {form.announcement}
        <Button
          type="submit"
          variant="primary"
          className={styles.submit}
          pending={recover.isPending}
        >
          {recover.isPending ? t('forgot.submitting') : t('forgot.submit')}
        </Button>
      </form>
      <div className={styles.links}>
        <Button variant="quiet" size="sm" onClick={onBack}>
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
