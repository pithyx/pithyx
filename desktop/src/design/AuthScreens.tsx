import { IconArrowLeft, IconCopy, IconKey, IconLanguage, IconPrinter } from '@tabler/icons-react';
import i18next from 'i18next';
import { useEffect, useState, type ReactNode, type SyntheticEvent } from 'react';
import { Form } from 'react-aria-components';
import { useTranslation } from 'react-i18next';
import { languages } from '../i18n';
import { AuthHeader, AuthLayout } from '../shell/AuthLayout';
import { Button } from '../ui/Button';
import { Checkbox } from '../ui/Controls';
import { Menu, MenuItem, MenuPopover, MenuTrigger } from '../ui/Menu';
import { cx } from '../ui/cx';
import { OtpField, PasswordField, TextField } from '../ui/TextField';
import styles from './AuthScreens.module.css';
import { hostname, recoveryKey, user } from './sampleData';

const MIN_PASSWORD_LENGTH = 12;

function LanguageMenu() {
  const { i18n } = useTranslation();
  const name = (code: string) =>
    new Intl.DisplayNames([code], { type: 'language' }).of(code) ?? code;
  return (
    <MenuTrigger>
      <Button variant="quiet" size="sm">
        <IconLanguage size={16} stroke={1.75} aria-hidden />
        {name(i18n.language)}
      </Button>
      <MenuPopover placement="top">
        <Menu onAction={(key) => void i18next.changeLanguage(String(key))}>
          {languages.map((code) => (
            <MenuItem key={code} id={code}>
              {name(code)}
            </MenuItem>
          ))}
        </Menu>
      </MenuPopover>
    </MenuTrigger>
  );
}

function preventSubmit(next: () => void) {
  return (event: SyntheticEvent<HTMLFormElement>) => {
    event.preventDefault();
    next();
  };
}

export function SetupAccount() {
  const { t } = useTranslation('auth');
  const { t: tDesktop } = useTranslation();
  return (
    <AuthLayout wide footer={<LanguageMenu />}>
      <AuthHeader host={hostname} product={tDesktop('brand')} />
      <Form
        className={styles.form}
        onSubmit={preventSubmit(() => undefined)}
        aria-labelledby="setup-title"
      >
        <div className={styles.heading}>
          <p className={styles.step}>{t('setup.step', { current: 1, total: 2 })}</p>
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
          defaultValue={user.username}
          isRequired
        />
        <TextField
          label={t('setup.displayName')}
          name="displayName"
          autoComplete="name"
          defaultValue={user.name}
        />
        <PasswordField
          label={t('setup.password')}
          description={t('setup.passwordHint', { minLength: MIN_PASSWORD_LENGTH })}
          name="password"
          autoComplete="new-password"
          minLength={MIN_PASSWORD_LENGTH}
          isRequired
        />
        <PasswordField
          label={t('setup.confirmPassword')}
          name="confirm"
          autoComplete="new-password"
          isRequired
        />
        <Button type="submit" variant="primary" className={styles.submit}>
          {t('setup.submit')}
        </Button>
      </Form>
    </AuthLayout>
  );
}

export function SetupRecovery() {
  const { t } = useTranslation('auth');
  const { t: tDesktop } = useTranslation();
  const [stored, setStored] = useState(false);
  return (
    <AuthLayout wide footer={<LanguageMenu />}>
      <AuthHeader host={hostname} product={tDesktop('brand')} />
      <section className={styles.form} aria-labelledby="recovery-title">
        <div className={styles.heading}>
          <p className={styles.step}>{t('setup.step', { current: 2, total: 2 })}</p>
          <h1 id="recovery-title" className={styles.title}>
            {t('recoveryKey.title')}
          </h1>
          <p className={styles.intro}>{t('recoveryKey.intro')}</p>
        </div>
        <div className={styles.key}>
          <IconKey size={20} stroke={1.75} aria-hidden className={styles.keyIcon} />
          <RecoveryKeyValue value={recoveryKey} />
        </div>
        <div className={styles.row}>
          <Button>
            <IconCopy size={18} stroke={1.75} aria-hidden />
            {t('recoveryKey.copy')}
          </Button>
          <Button>
            <IconPrinter size={18} stroke={1.75} aria-hidden />
            {t('recoveryKey.print')}
          </Button>
        </div>
        <Checkbox isSelected={stored} onChange={setStored}>
          {t('recoveryKey.confirm')}
        </Checkbox>
        <Button variant="primary" isDisabled={!stored} className={styles.submit}>
          {t('recoveryKey.continue')}
        </Button>
      </section>
    </AuthLayout>
  );
}

export type LoginStep = 'password' | 'forgot' | 'totp' | 'welcome' | 'leaving' | 'desktop';

/**
 * A clickable sign-in: password, then the TOTP step in the same card, then the welcome state for as long as
 * loading takes (simulated), then the desktop fades in behind the card.
 */
export function LoginFlow({
  initial,
  loadingMs = 1400,
  desktop,
  controls,
}: {
  initial: LoginStep;
  loadingMs?: number;
  desktop: (entering: boolean) => ReactNode;
  controls: (replay: () => void) => ReactNode;
}) {
  const [step, setStep] = useState<LoginStep>(initial);
  const [run, setRun] = useState(0);

  // Simulates the desktop loading after sign-in; the welcome state never adds its own delay.
  useEffect(() => {
    if (step !== 'welcome') {
      return;
    }
    const timer = window.setTimeout(() => {
      setStep('leaving');
    }, loadingMs);
    return () => {
      window.clearTimeout(timer);
    };
  }, [step, loadingMs, run]);

  const showDesktop = step === 'leaving' || step === 'desktop';
  return (
    <div className={styles.flow} key={run}>
      {showDesktop && desktop(true)}
      {step !== 'desktop' && (
        <AuthLayout
          footer={step === 'welcome' || step === 'leaving' ? undefined : <LanguageMenu />}
          leaving={step === 'leaving'}
          onLeft={() => {
            setStep('desktop');
          }}
        >
          {step === 'password' && (
            <PasswordStep
              onNext={() => {
                setStep('totp');
              }}
              onForgot={() => {
                setStep('forgot');
              }}
            />
          )}
          {step === 'forgot' && (
            <ForgotStep
              onBack={() => {
                setStep('password');
              }}
            />
          )}
          {step === 'totp' && (
            <TotpStep
              onBack={() => {
                setStep('password');
              }}
              onNext={() => {
                setStep('welcome');
              }}
            />
          )}
          {(step === 'welcome' || step === 'leaving') && <WelcomeStep />}
        </AuthLayout>
      )}
      {controls(() => {
        setStep(initial === 'desktop' ? 'welcome' : initial);
        setRun(run + 1);
      })}
    </div>
  );
}

function PasswordStep({ onNext, onForgot }: { onNext: () => void; onForgot: () => void }) {
  const { t } = useTranslation('auth');
  const { t: tDesktop } = useTranslation();
  return (
    <>
      <AuthHeader host={hostname} product={tDesktop('brand')} />
      <Form className={styles.form} onSubmit={preventSubmit(onNext)} aria-labelledby="login-title">
        <h1 id="login-title" className={styles.title}>
          {t('login.heading')}
        </h1>
        <TextField
          label={t('login.username')}
          name="username"
          autoComplete="username"
          defaultValue={user.username}
          autoFocus
          isRequired
        />
        <PasswordField
          label={t('login.password')}
          name="password"
          autoComplete="current-password"
          isRequired
        />
        <Button type="submit" variant="primary" className={styles.submit}>
          {t('login.submit')}
        </Button>
        <div className={styles.links}>
          <Button variant="quiet" size="sm" onPress={onForgot}>
            {t('login.forgot')}
          </Button>
        </div>
      </Form>
    </>
  );
}

/** Two lines of four groups; the key only breaks at a hyphen. */
function RecoveryKeyValue({ value }: { value: string }) {
  const groups = value.split('-');
  return (
    <code className={styles.keyValue}>
      <span className={styles.keyLine}>{`${groups.slice(0, 4).join('-')}-`}</span>
      <span className={styles.keyLine}>{groups.slice(4).join('-')}</span>
    </code>
  );
}

/** No reset by email: users ask an administrator, administrators use the recovery key (POST /api/auth/recover). */
function ForgotStep({ onBack }: { onBack: () => void }) {
  const { t } = useTranslation('auth');
  return (
    <Form className={styles.form} onSubmit={preventSubmit(onBack)} aria-labelledby="forgot-title">
      <div className={styles.heading}>
        <h1 id="forgot-title" className={styles.title}>
          {t('forgot.title')}
        </h1>
        <p className={styles.intro}>{t('forgot.intro')}</p>
      </div>
      <section className={cx(styles.form, styles.adminBox)} aria-labelledby="forgot-admin">
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
          defaultValue={user.username}
          isRequired
        />
        <TextField
          label={t('forgot.recoveryKey')}
          name="recoveryKey"
          autoComplete="off"
          spellCheck="false"
          inputClassName={styles.mono}
          isRequired
        />
        <PasswordField
          label={t('forgot.newPassword')}
          name="newPassword"
          autoComplete="new-password"
          isRequired
        />
        <Button type="submit" variant="primary" className={styles.submit}>
          {t('forgot.submit')}
        </Button>
      </section>
      <div className={styles.links}>
        <Button variant="quiet" size="sm" onPress={onBack}>
          <IconArrowLeft size={16} stroke={1.75} aria-hidden />
          {t('forgot.back')}
        </Button>
      </div>
    </Form>
  );
}

function TotpStep({ onBack, onNext }: { onBack: () => void; onNext: () => void }) {
  const { t } = useTranslation('auth');
  return (
    <Form className={styles.form} onSubmit={preventSubmit(onNext)} aria-labelledby="totp-title">
      <div className={styles.userRow}>
        <Button variant="quiet" size="sm" onPress={onBack} aria-label={t('totp.back')}>
          <IconArrowLeft size={18} stroke={1.75} aria-hidden />
        </Button>
        <span className={styles.avatar} aria-hidden>
          {user.initials}
        </span>
        <span className={styles.userName}>{user.username}</span>
      </div>
      <h1 id="totp-title" className={styles.title}>
        {t('totp.title')}
      </h1>
      <OtpField label={t('totp.code')} name="code" autoFocus isRequired />
      <Button type="submit" variant="primary" className={styles.submit}>
        {t('totp.submit')}
      </Button>
      <div className={styles.links}>
        <Button variant="quiet" size="sm">
          {t('totp.useRecoveryCode')}
        </Button>
      </div>
    </Form>
  );
}

function WelcomeStep() {
  const { t } = useTranslation('auth');
  return (
    <div className={styles.welcome} role="status">
      <span className={styles.bigAvatar} aria-hidden>
        {user.initials}
      </span>
      <h1 className={styles.welcomeTitle}>{t('login.welcome', { name: user.firstName })}</h1>
      <div className={styles.preparing}>
        <p>{t('login.preparing')}</p>
        <span className={styles.indeterminate} aria-hidden />
      </div>
    </div>
  );
}
