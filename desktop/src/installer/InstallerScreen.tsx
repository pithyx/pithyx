import { useMutation, useQuery, useQueryClient, type QueryClient } from '@tanstack/react-query';
import type { TFunction } from 'i18next';
import { unwrap, type components, type ErrorCode } from '@pithyx/api-client';
import { useEffect, useId, useState, type ReactNode, type SyntheticEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { api } from '../api/client';
import { Button, Checkbox, Field, LanguageSelect, PasswordField } from '../auth/controls';
import styles from '../auth/Auth.module.css';
import { useFormErrors } from '../auth/formErrors';
import { NewPasswordField } from '../auth/password/NewPasswordField';
import { useNewPasswordCheck } from '../auth/password/PasswordStrengthMeter';
import { RecoveryKeyCard } from '../auth/RecoveryKeyCard';
import { describeError, type DescribedError } from '../errors/describe';
import { ErrorNotice } from '../errors/ErrorNotice';
import { field } from '../forms';
import { languageName, languages, setLanguage, useFormatLocale, type Language } from '../i18n';
import { REGIONS } from '../regions';
import { AuthHeader, AuthLayout } from '../shell/AuthLayout';
import { IconAlertTriangle, IconAlertCircle, IconCheck, IconLoader2 } from '../ui/icons';
import { IpFields } from '../network/IpFields';
import {
  defaultInterface,
  hostAfterChange,
  ipFormOf,
  splitList,
  withInterface,
  withIp,
  withoutInterface,
  type NetworkConfig,
} from '../network/model';
import own from './Installer.module.css';

type Status = components['schemas']['InstallerStatus'];
type Options = components['schemas']['InstallerOptions'];
type OfferedDisk = components['schemas']['InstallerDisk'];

const statusKey = ['installer', 'status'] as const;
const networkTestKey = ['installer', 'network', 'test'] as const;
const STEPS = 5;

interface Choices {
  hostname: string;
  language: Language;
  region: string | null;
  timeZone: string;
  ntpServers: string;
  keyboardLayout: string;
  firstDisk: string | null;
  mirror: boolean;
  secondDisk: string | null;
  username: string;
  displayName: string;
  password: string;
  /** The network of step 2; null keeps the live system's settings (DHCP, or what the console set). */
  network: NetworkConfig | null;
}

// The console keyboard follows the region, else the language; the user can change it.
const KEYBOARD_BY_REGION: Record<string, string> = {
  AT: 'at',
  BE: 'be',
  CH: 'ch',
  CZ: 'cz',
  DE: 'de',
  DK: 'dk',
  ES: 'es',
  FI: 'fi',
  FR: 'fr',
  GB: 'gb',
  IE: 'gb',
  IT: 'it',
  NL: 'us',
  NO: 'no',
  PL: 'pl',
  PT: 'pt',
  SE: 'se',
  US: 'us',
};

function keyboardFor(region: string | null, language: Language): string {
  return (
    (region === null ? undefined : KEYBOARD_BY_REGION[region]) ?? (language === 'de' ? 'de' : 'us')
  );
}

/** The browser's region, if it is one the installer offers. */
function browserRegion(): string | null {
  const region = new Intl.Locale(navigator.language).maximize().region;
  return region !== undefined && (REGIONS as readonly string[]).includes(region) ? region : null;
}

function formatSize(bytes: number, locale: string): string {
  const terabytes = bytes >= 1e12;
  return new Intl.NumberFormat(locale, {
    style: 'unit',
    unit: terabytes ? 'terabyte' : 'gigabyte',
    maximumFractionDigits: terabytes ? 1 : 0,
  }).format(bytes / (terabytes ? 1e12 : 1e9));
}

/**
 * The web installer of the live system (D294): the first browser claims the installation, checks the requirements,
 * walks through name and region, network, system disk, administrator and a summary, follows the installation, shows
 * the recovery key, restarts the box and opens the installed system once it answers.
 */
export function InstallerScreen() {
  const { t, i18n } = useTranslation('installer');
  const claim = useQuery({
    queryKey: ['installer', 'claim'],
    queryFn: async () => unwrap(await api.POST('/api/installer/claim')),
    retry: false,
    staleTime: Infinity,
  });
  const status = useQuery({
    queryKey: statusKey,
    // A live system that restarted since the page claimed it has no owner; this page takes it again.
    queryFn: async ({ signal }) => {
      const current = unwrap(await api.GET('/api/installer', { signal }));
      return current.claimed ? current : unwrap(await api.POST('/api/installer/claim'));
    },
    enabled: claim.isSuccess,
    initialData: claim.data,
    refetchInterval: (query) => (query.state.data?.phase === 'installing' ? 1000 : false),
  });
  const options = useQuery({
    queryKey: ['installer', 'options'],
    queryFn: async ({ signal }) => unwrap(await api.GET('/api/installer/options', { signal })),
    enabled: claim.isSuccess,
  });
  const [step, setStep] = useState(0);
  const [returned, setReturned] = useState<{ step: number; error: DescribedError }>();
  // After a failed installation the user may go back through the steps and start it again.
  const [editing, setEditing] = useState(false);
  const [choices, setChoices] = useState<Choices>(() => {
    const language = (languages as readonly string[]).includes(i18n.language)
      ? (i18n.language as Language)
      : 'en';
    const region = browserRegion();
    return {
      hostname: 'pithyx',
      language,
      region,
      timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
      ntpServers: '',
      keyboardLayout: keyboardFor(region, language),
      firstDisk: null,
      mirror: false,
      secondDisk: null,
      username: '',
      displayName: '',
      password: '',
      network: null,
    };
  });

  let content: ReactNode;
  if (claim.isError) {
    const error = describeError(claim.error);
    content =
      error.code === 'installer.claim.taken' ? (
        <Message title={t('taken.title')} intro={t('taken.intro')} />
      ) : (
        <ErrorNotice error={error} />
      );
  } else if (status.data === undefined || options.data === undefined) {
    content = null;
  } else if (status.data.phase !== 'ready' && !(status.data.phase === 'failed' && editing)) {
    content = (
      <Installation
        status={status.data}
        host={hostAfterChange(
          options.data.network,
          choices.network ?? options.data.networkConfig,
          location.hostname,
        )}
        onChange={() => {
          setEditing(true);
          setReturned(undefined);
          setStep(5);
        }}
      />
    );
  } else {
    const current = status.data;
    const offered = options.data;
    const update = (next: Partial<Choices>) => {
      setChoices((previous) => ({ ...previous, ...next }));
    };
    // Leaving a step drops the daemon's error that sent the user back to it.
    const go = (next: number) => {
      setReturned(undefined);
      setStep(next);
    };
    const pages = [
      <Welcome
        key="welcome"
        status={current}
        onNext={() => {
          // The language picked on the welcome page carries into the region step.
          if ((languages as readonly string[]).includes(i18n.language)) {
            const language = i18n.language as Language;
            update({ language, keyboardLayout: keyboardFor(choices.region, language) });
          }
          go(1);
        }}
      />,
      <RegionStep
        key="region"
        choices={choices}
        options={offered}
        update={update}
        onBack={() => {
          go(0);
        }}
        onNext={() => {
          go(2);
        }}
      />,
      <NetworkStep
        key="network"
        options={offered}
        config={choices.network ?? offered.networkConfig}
        onChange={(network) => {
          update({ network });
        }}
        onBack={() => {
          go(1);
        }}
        onNext={() => {
          go(3);
        }}
      />,
      <DiskStep
        key="disks"
        choices={choices}
        options={offered}
        update={update}
        onBack={() => {
          go(2);
        }}
        onNext={() => {
          go(4);
        }}
      />,
      <AdminStep
        key="admin"
        choices={choices}
        update={update}
        onBack={() => {
          go(3);
        }}
        onNext={() => {
          go(5);
        }}
      />,
      <Summary
        key="summary"
        choices={choices}
        options={offered}
        onBack={() => {
          go(4);
        }}
        onFieldError={(next, error) => {
          setStep(next);
          setReturned({ step: next, error });
        }}
        onStarted={() => {
          setEditing(false);
        }}
      />,
    ];
    content =
      returned?.step === step ? (
        <>
          <ErrorNotice error={returned.error} />
          {pages[step]}
        </>
      ) : (
        pages[step]
      );
  }

  return (
    <AuthLayout wide footer={step === 0 && !claim.isError ? <LanguageSelect /> : undefined}>
      <AuthHeader host={choices.hostname} motion={step === 0 ? 'welcome' : 'loop'} />
      {content}
    </AuthLayout>
  );
}

function Message({
  title,
  intro,
  children,
}: {
  title: string;
  intro: string;
  children?: ReactNode;
}) {
  const titleId = useId();
  return (
    <section className={styles.form} aria-labelledby={titleId}>
      <div className={styles.heading}>
        <h1 id={titleId} className={styles.title}>
          {title}
        </h1>
        <p className={styles.intro}>{intro}</p>
      </div>
      {children}
    </section>
  );
}

function StepForm({
  index,
  title,
  intro,
  onBack,
  onSubmit,
  submitLabel,
  pending = false,
  children,
}: {
  index: number;
  title: string;
  intro?: string;
  onBack: () => void;
  onSubmit: (event: SyntheticEvent<HTMLFormElement>) => void;
  submitLabel?: string;
  pending?: boolean;
  children: ReactNode;
}) {
  const { t } = useTranslation('installer');
  const titleId = useId();
  return (
    <form
      className={styles.form}
      noValidate
      aria-labelledby={titleId}
      onSubmit={(event) => {
        event.preventDefault();
        onSubmit(event);
      }}
    >
      <div className={styles.heading}>
        <p className={styles.step}>{t('step', { current: index, total: STEPS })}</p>
        <h1 id={titleId} className={styles.title}>
          {title}
        </h1>
        {intro !== undefined && <p className={styles.intro}>{intro}</p>}
      </div>
      {children}
      <div className={styles.row}>
        <Button onClick={onBack} disabled={pending}>
          {t('back')}
        </Button>
        <Button type="submit" variant="primary" pending={pending}>
          {submitLabel ?? t('next')}
        </Button>
      </div>
    </form>
  );
}

function Select({
  label,
  value,
  onChange,
  children,
}: {
  label: string;
  value: string;
  onChange: (value: string) => void;
  children: ReactNode;
}) {
  return (
    <label className={own.selectField}>
      <span className={own.label}>{label}</span>
      <select
        className={own.select}
        value={value}
        onChange={(event) => {
          onChange(event.target.value);
        }}
      >
        {children}
      </select>
    </label>
  );
}

function Welcome({ status, onNext }: { status: Status; onNext: () => void }) {
  const { t } = useTranslation('installer');
  const titleId = useId();
  const passed = status.checks.every((check) => check.passed);
  return (
    <section className={styles.form} aria-labelledby={titleId}>
      <div className={styles.heading}>
        <h1 id={titleId} className={styles.title}>
          {t('welcome.title')}
        </h1>
        <p className={own.tagline}>{t('welcome.tagline')}</p>
        <p className={styles.intro}>{t('welcome.intro', { version: status.version })}</p>
      </div>
      <h2 className={own.subtitle}>{t('welcome.checksTitle')}</h2>
      <ul className={own.list}>
        {status.checks.map((check) => (
          <li key={check.id} className={own.check} data-passed={check.passed}>
            {check.passed ? (
              <IconCheck size={18} stroke={2} aria-hidden className={own.ok} />
            ) : (
              <IconAlertCircle size={18} stroke={2} aria-hidden className={own.bad} />
            )}
            <span>{checkText(t, check)}</span>
          </li>
        ))}
      </ul>
      <Button
        variant="primary"
        className={styles.submit}
        disabled={!passed}
        autoFocus
        onClick={onNext}
      >
        {t('welcome.start')}
      </Button>
    </section>
  );
}

const CHECK_TEXTS = {
  cpu: ['checks.cpu', 'checks.cpuFailed'],
  uefi: ['checks.uefi', 'checks.uefiFailed'],
  memory: ['checks.memory', 'checks.memoryFailed'],
  secure_boot: ['checks.secure_boot', 'checks.secure_bootFailed'],
  disk: ['checks.disk', 'checks.diskFailed'],
} as const;

function checkText(t: TFunction<'installer'>, check: Status['checks'][number]): string {
  const texts = CHECK_TEXTS[check.id as keyof typeof CHECK_TEXTS] as
    (typeof CHECK_TEXTS)[keyof typeof CHECK_TEXTS] | undefined;
  const params = check.params as Record<string, string | number | boolean>;
  if (check.id === 'memory' && check.passed && params.low === true) {
    // Enough to install, but less than recommended: a hint, not a refusal (D363).
    return t('checks.memoryLow', params as Record<string, string | number>);
  }
  return texts === undefined
    ? check.id
    : t(texts[check.passed ? 0 : 1], params as Record<string, string | number>);
}

function RegionStep({
  choices,
  options,
  update,
  onBack,
  onNext,
}: {
  choices: Choices;
  options: Options;
  update: (next: Partial<Choices>) => void;
  onBack: () => void;
  onNext: () => void;
}) {
  const { t, i18n } = useTranslation('installer');
  const { t: tErrors } = useTranslation('errors');
  const [error, setError] = useState<string>();
  const countries = new Intl.DisplayNames([i18n.language], { type: 'region' });
  const regions = REGIONS.map((code) => ({ code, name: countries.of(code) ?? code })).sort((a, b) =>
    a.name.localeCompare(b.name, i18n.language),
  );
  const zones = options.timeZones.includes(choices.timeZone)
    ? options.timeZones
    : [choices.timeZone, ...options.timeZones];
  return (
    <StepForm
      index={1}
      title={t('region.title')}
      onBack={onBack}
      onSubmit={() => {
        if (!/^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$/.test(choices.hostname)) {
          setError(tErrors('installer.hostname.invalid'));
          return;
        }
        onNext();
      }}
    >
      <Field
        label={t('region.hostname')}
        description={t('region.hostnameHint')}
        error={error}
        name="hostname"
        value={choices.hostname}
        autoCapitalize="none"
        autoComplete="off"
        spellCheck={false}
        autoFocus
        onChange={(event) => {
          setError(undefined);
          update({ hostname: event.target.value.trim().toLowerCase() });
        }}
      />
      <Select
        label={t('region.language')}
        value={choices.language}
        onChange={(value) => {
          const language = value as Language;
          update({ language, keyboardLayout: keyboardFor(choices.region, language) });
          void setLanguage(language);
        }}
      >
        {languages.map((code) => (
          <option key={code} value={code} lang={code}>
            {languageName(code)}
          </option>
        ))}
      </Select>
      <Select
        label={t('region.region')}
        value={choices.region ?? ''}
        onChange={(value) => {
          const region = value === '' ? null : value;
          update({ region, keyboardLayout: keyboardFor(region, choices.language) });
        }}
      >
        <option value="">{t('region.regionAuto')}</option>
        {regions.map((region) => (
          <option key={region.code} value={region.code}>
            {region.name}
          </option>
        ))}
      </Select>
      <Select
        label={t('region.timeZone')}
        value={choices.timeZone}
        onChange={(value) => {
          update({ timeZone: value });
        }}
      >
        {zones.map((zone) => (
          <option key={zone} value={zone}>
            {zone.replaceAll('_', ' ')}
          </option>
        ))}
      </Select>
      <Select
        label={t('region.keyboard')}
        value={choices.keyboardLayout}
        onChange={(value) => {
          update({ keyboardLayout: value });
        }}
      >
        {options.keyboardLayouts.map((layout) => (
          <option key={layout} value={layout}>
            {layout}
          </option>
        ))}
      </Select>
      <Field
        label={t('region.ntp')}
        description={t('region.ntpHint')}
        name="ntp"
        value={choices.ntpServers}
        autoCapitalize="none"
        autoComplete="off"
        spellCheck={false}
        onChange={(event) => {
          update({ ntpServers: event.target.value });
        }}
      />
    </StepForm>
  );
}

function NetworkStep({
  options,
  config,
  onChange,
  onBack,
  onNext,
}: {
  options: Options;
  config: NetworkConfig;
  onChange: (next: NetworkConfig) => void;
  onBack: () => void;
  onNext: () => void;
}) {
  const { t } = useTranslation('installer');
  const queryClient = useQueryClient();
  const cards = options.network.filter((link) => link.kind === 'ethernet');
  const start = useMutation({
    mutationFn: async () => unwrap(await api.POST('/api/installer/network/test', { body: config })),
    onSuccess: (result) => {
      queryClient.setQueryData(networkTestKey, result);
    },
    onError: (failure) => {
      reclaimIfLost(queryClient, failure);
    },
  });
  // The page may lose its connection while the live system tries the settings; it asks again until the test ends.
  const test = useQuery({
    queryKey: networkTestKey,
    queryFn: async ({ signal }) => unwrap(await api.GET('/api/installer/network/test', { signal })),
    enabled: start.isSuccess,
    refetchInterval: (query) => (query.state.data?.running === false ? false : 2000),
    retry: true,
  });
  const result = start.isSuccess ? test.data : undefined;
  return (
    <StepForm
      index={2}
      title={t('network.title')}
      intro={t('network.intro')}
      onBack={onBack}
      onSubmit={onNext}
    >
      {cards.length === 0 ? (
        <p className={styles.intro}>{t('network.none')}</p>
      ) : (
        <ul className={own.list}>
          {cards.map((nic) => {
            const item = config.interfaces.find((candidate) => candidate.name === nic.name);
            return (
              <li key={nic.name} className={own.item}>
                <span className={own.itemTitle}>
                  <span className={styles.mono}>{nic.name}</span>
                  <span className={own.badge} data-ok={nic.carrier}>
                    {nic.carrier ? t('network.connected') : t('network.disconnected')}
                  </span>
                </span>
                <span className={own.itemDetail}>
                  {nic.addresses.length === 0 ? t('network.noAddress') : nic.addresses.join(', ')}
                </span>
                <details className={own.details} open={item !== undefined}>
                  <summary>{t('network.configure', { name: nic.name })}</summary>
                  <IpFields
                    form={ipFormOf(item)}
                    onChange={(form) => {
                      const next = withIp(item ?? defaultInterface(nic.name), form);
                      // DHCP and automatic IPv6 is what a card without settings gets; it keeps no settings then.
                      onChange(
                        next.ipv4 === 'dhcp' && next.ipv6 === 'auto' && (next.mtu ?? null) === null
                          ? withoutInterface(config, nic.name)
                          : withInterface(config, next),
                      );
                      start.reset();
                    }}
                  />
                </details>
              </li>
            );
          })}
        </ul>
      )}
      <Field
        label={t('network.dns')}
        description={t('network.dnsHint')}
        name="dns"
        value={config.global.dnsServers.join(' ')}
        autoCapitalize="none"
        autoComplete="off"
        spellCheck={false}
        onChange={(event) => {
          onChange({
            ...config,
            global: { ...config.global, dnsServers: splitList(event.target.value) },
          });
          start.reset();
        }}
      />
      <div>
        <Button
          type="button"
          variant="secondary"
          pending={start.isPending || result?.running === true}
          onClick={() => {
            start.mutate();
          }}
        >
          {t('network.test')}
        </Button>
      </div>
      {(start.isPending || result?.running === true) && (
        <p className={styles.intro} role="status">
          {t('network.testing')}
        </p>
      )}
      {start.isError && <ErrorNotice error={describeError(start.error)} />}
      {result !== undefined && !result.running && result.errorCode !== null && (
        <ErrorNotice
          error={{
            code: result.errorCode as ErrorCode,
            params: result.errorParams,
            requestId: '',
          }}
        />
      )}
      {result !== undefined && !result.running && (
        <ul className={own.list} aria-live="polite">
          {result.interfaces.map((tested) => (
            <li key={tested.name} className={own.check}>
              {tested.addresses.length > 0 && tested.gatewayReachable !== false ? (
                <IconCheck size={18} stroke={2} aria-hidden className={own.ok} />
              ) : (
                <IconAlertTriangle size={18} stroke={2} aria-hidden className={own.bad} />
              )}
              <span>
                {tested.addresses.length === 0
                  ? t('network.testNoAddress', { name: tested.name })
                  : t('network.testOk', {
                      name: tested.name,
                      addresses: tested.addresses.join(', '),
                    })}{' '}
                {tested.gatewayReachable === true &&
                  t('network.testGateway', { gateway: tested.gateways[0] ?? '' })}
                {tested.gatewayReachable === false &&
                  t('network.testNoGateway', { gateway: tested.gateways[0] ?? '' })}
              </span>
            </li>
          ))}
        </ul>
      )}
    </StepForm>
  );
}

function DiskOption({
  disk,
  name,
  checked,
  disabled,
  minGb,
  onChange,
}: {
  disk: OfferedDisk;
  name: string;
  checked: boolean;
  disabled: boolean;
  minGb: number;
  onChange: () => void;
}) {
  const { t } = useTranslation('installer');
  const locale = useFormatLocale();
  const reason = disk.reason ?? undefined;
  return (
    <label className={own.disk} data-disabled={!disk.suitable || disabled || undefined}>
      <input
        type="radio"
        name={name}
        checked={checked}
        disabled={!disk.suitable || disabled}
        onChange={onChange}
      />
      <span className={own.itemText}>
        <span className={own.itemTitle}>
          {disk.disk.model ?? disk.disk.name}
          <span className={styles.mono}>{disk.disk.path}</span>
        </span>
        <span className={own.itemDetail}>
          {t('disks.details', {
            size: formatSize(disk.disk.sizeBytes, locale),
            serial: disk.disk.serial ?? t('disks.noSerial'),
          })}
        </span>
        {reason !== undefined && (
          <span className={own.itemDetail}>
            {t('disks.unsuitable', {
              reason: t(`disks.reason.${reason}` as 'disks.reason.too_small', { minGb }),
            })}
          </span>
        )}
        {disk.suitable && disk.caution !== null && (
          <span className={own.warning}>
            <IconAlertTriangle size={16} stroke={2} aria-hidden />
            {t(`disks.caution.${disk.caution}` as 'disks.caution.usb')}
          </span>
        )}
        {disk.suitable && disk.disk.rotational && (
          <span className={own.hint}>{t('disks.hdd')}</span>
        )}
        {disk.suitable && disk.hasData && (
          <span className={own.warning}>
            <IconAlertTriangle size={16} stroke={2} aria-hidden />
            {t('disks.hasData')}
          </span>
        )}
      </span>
    </label>
  );
}

function DiskStep({
  choices,
  options,
  update,
  onBack,
  onNext,
}: {
  choices: Choices;
  options: Options;
  update: (next: Partial<Choices>) => void;
  onBack: () => void;
  onNext: () => void;
}) {
  const { t } = useTranslation('installer');
  const [error, setError] = useState<string>();
  const minGb = Math.round(options.minDiskBytes / 1e9);
  const suitable = options.disks.filter((disk) => disk.suitable);
  return (
    <StepForm
      index={3}
      title={t('disks.title')}
      intro={t('disks.intro')}
      onBack={onBack}
      onSubmit={() => {
        if (choices.firstDisk === null) {
          setError(t('disks.choose'));
        } else if (choices.mirror && choices.secondDisk === null) {
          setError(t('disks.chooseSecond'));
        } else {
          onNext();
        }
      }}
    >
      <fieldset className={own.fieldset}>
        <legend className={own.label}>{t('disks.first')}</legend>
        {options.disks.map((disk) => (
          <DiskOption
            key={disk.disk.id}
            disk={disk}
            name="first"
            minGb={minGb}
            checked={choices.firstDisk === disk.disk.id}
            disabled={false}
            onChange={() => {
              setError(undefined);
              update({
                firstDisk: disk.disk.id,
                secondDisk: choices.secondDisk === disk.disk.id ? null : choices.secondDisk,
              });
            }}
          />
        ))}
      </fieldset>
      {suitable.length >= 2 && (
        <div className={own.mirror}>
          <Checkbox
            checked={choices.mirror}
            onChange={(mirror) => {
              setError(undefined);
              update({ mirror, secondDisk: mirror ? choices.secondDisk : null });
            }}
          >
            {t('disks.mirror')}
          </Checkbox>
          <p className={own.hint}>{t('disks.mirrorHint')}</p>
        </div>
      )}
      {choices.mirror && (
        <fieldset className={own.fieldset}>
          <legend className={own.label}>{t('disks.second')}</legend>
          {suitable.map((disk) => (
            <DiskOption
              key={disk.disk.id}
              disk={disk}
              name="second"
              minGb={minGb}
              checked={choices.secondDisk === disk.disk.id}
              disabled={disk.disk.id === choices.firstDisk}
              onChange={() => {
                setError(undefined);
                update({ secondDisk: disk.disk.id });
              }}
            />
          ))}
        </fieldset>
      )}
      {error !== undefined && (
        <p className={own.error} role="alert">
          {error}
        </p>
      )}
    </StepForm>
  );
}

type AdminField = 'username' | 'displayName' | 'password' | 'confirm';
const ADMIN_FIELDS: readonly AdminField[] = ['username', 'displayName', 'password', 'confirm'];

function AdminStep({
  choices,
  update,
  onBack,
  onNext,
}: {
  choices: Choices;
  update: (next: Partial<Choices>) => void;
  onBack: () => void;
  onNext: () => void;
}) {
  const { t } = useTranslation('installer');
  const { t: tAuth } = useTranslation('auth');
  const form = useFormErrors<AdminField>(ADMIN_FIELDS);
  const checkPassword = useNewPasswordCheck();
  return (
    <StepForm
      index={4}
      title={t('admin.title')}
      intro={t('admin.intro')}
      onBack={onBack}
      onSubmit={(event) => {
        const data = new FormData(event.currentTarget);
        const username = field(data, 'username').trim();
        const password = field(data, 'password');
        const errors: Partial<Record<AdminField, string>> = {};
        if (username === '') {
          errors.username = tAuth('validation.usernameRequired');
        }
        const passwordError = checkPassword(password);
        if (password === '') {
          errors.password = tAuth('validation.passwordRequired');
        } else if (passwordError !== undefined) {
          errors.password = passwordError;
        } else if (password !== field(data, 'confirm')) {
          errors.confirm = tAuth('setup.passwordMismatch');
        }
        if (form.check(errors)) {
          update({ username, displayName: field(data, 'displayName').trim(), password });
          onNext();
        }
      }}
    >
      <Field
        {...form.register('username')}
        label={tAuth('setup.username')}
        description={tAuth('setup.usernameHint')}
        name="username"
        defaultValue={choices.username}
        autoComplete="username"
        autoCapitalize="none"
        spellCheck={false}
        autoFocus
        required
      />
      <Field
        {...form.register('displayName')}
        label={tAuth('setup.displayName')}
        name="displayName"
        defaultValue={choices.displayName}
        autoComplete="name"
      />
      <NewPasswordField
        {...form.register('password')}
        label={tAuth('setup.password')}
        name="password"
        defaultValue={choices.password}
        required
      />
      <PasswordField
        {...form.register('confirm')}
        label={tAuth('setup.confirmPassword')}
        name="confirm"
        defaultValue={choices.password}
        autoComplete="new-password"
        required
      />
      {form.announcement}
    </StepForm>
  );
}

/** After a restart of the live system nobody owns it; reading the status claims it again (see the status query). */
function reclaimIfLost(queryClient: QueryClient, failure: unknown) {
  if (describeError(failure).code === 'installer.claim.required') {
    void queryClient.invalidateQueries({ queryKey: statusKey });
  }
}

/** The step a daemon error sends the user back to. */
function stepOf(code: string): number | undefined {
  if (code.startsWith('auth.')) {
    return 4;
  }
  if (code.startsWith('installer.disk.')) {
    return 3;
  }
  if (code.startsWith('network.')) {
    return 2;
  }
  if (
    code === 'installer.hostname.invalid' ||
    code.startsWith('system.time.') ||
    code === 'installer.keyboard.invalid'
  ) {
    return 1;
  }
  return undefined;
}

function Summary({
  choices,
  options,
  onBack,
  onFieldError,
  onStarted,
}: {
  choices: Choices;
  options: Options;
  onBack: () => void;
  onFieldError: (step: number, error: DescribedError) => void;
  onStarted: () => void;
}) {
  const { t } = useTranslation('installer');
  const queryClient = useQueryClient();
  const locale = useFormatLocale();
  const [confirmed, setConfirmed] = useState(false);
  const [confirmMissing, setConfirmMissing] = useState(false);
  const [error, setError] = useState<DescribedError>();
  const diskIds = [choices.firstDisk, choices.mirror ? choices.secondDisk : null].filter(
    (id): id is string => id !== null,
  );
  const disks = options.disks.filter((disk) => diskIds.includes(disk.disk.id));
  const install = useMutation({
    mutationFn: async () =>
      unwrap(
        await api.POST('/api/installer/install', {
          body: {
            hostname: choices.hostname,
            language: choices.language,
            region: choices.region,
            timeZone: choices.timeZone,
            ntpServers: choices.ntpServers
              .split(',')
              .map((server) => server.trim())
              .filter((server) => server !== ''),
            keyboardLayout: choices.keyboardLayout,
            diskIds,
            username: choices.username,
            password: choices.password,
            displayName: choices.displayName === '' ? null : choices.displayName,
            eraseConfirmed: confirmed,
            network: choices.network ?? options.networkConfig,
          },
        }),
      ),
    onSuccess: (status) => {
      queryClient.setQueryData(statusKey, status);
      onStarted();
    },
    onError: (failure) => {
      reclaimIfLost(queryClient, failure);
      const described = describeError(failure);
      const step = stepOf(described.code);
      if (step === undefined) {
        setError(described);
      } else {
        onFieldError(step, described);
      }
    },
  });
  const rows: [string, string][] = [
    [t('summary.name'), choices.hostname],
    [t('summary.language'), languageName(choices.language)],
    [t('summary.timeZone'), choices.timeZone.replaceAll('_', ' ')],
    [t('summary.keyboard'), choices.keyboardLayout],
    [
      t('summary.administrator'),
      choices.displayName === ''
        ? choices.username
        : `${choices.displayName} (${choices.username})`,
    ],
  ];
  return (
    <StepForm
      index={5}
      title={t('summary.title')}
      onBack={onBack}
      submitLabel={t('summary.install')}
      pending={install.isPending}
      onSubmit={() => {
        if (confirmed) {
          setError(undefined);
          install.mutate();
        } else {
          setConfirmMissing(true);
        }
      }}
    >
      <dl className={own.summary}>
        {rows.map(([term, value]) => (
          <div key={term} className={own.summaryRow}>
            <dt>{term}</dt>
            <dd>{value}</dd>
          </div>
        ))}
      </dl>
      <h2 className={own.subtitle}>{t('summary.erased')}</h2>
      <ul className={own.list}>
        {disks.map((disk) => (
          <li key={disk.disk.id} className={own.item}>
            <span className={own.itemTitle}>
              {disk.disk.model ?? disk.disk.name}
              <span className={styles.mono}>{disk.disk.path}</span>
            </span>
            <span className={own.itemDetail}>
              {t('disks.details', {
                size: formatSize(disk.disk.sizeBytes, locale),
                serial: disk.disk.serial ?? t('disks.noSerial'),
              })}
            </span>
          </li>
        ))}
      </ul>
      <Checkbox
        checked={confirmed}
        onChange={(checked) => {
          setConfirmed(checked);
          setConfirmMissing(false);
        }}
      >
        {t('summary.confirm')}
      </Checkbox>
      {confirmMissing && (
        <p className={own.error} role="alert">
          {t('summary.confirmRequired')}
        </p>
      )}
      {error !== undefined && <ErrorNotice error={error} />}
    </StepForm>
  );
}

/** The install id the box at this address answers on port 80, or null while it does not answer yet. */
async function probeInstalledSystem(host: string, signal: AbortSignal): Promise<string | null> {
  try {
    const response = await fetch(`http://${host}/api/install-id`, {
      signal,
      cache: 'no-store',
    });
    if (!response.ok) {
      return null;
    }
    const body = (await response.json()) as { installId?: string };
    return body.installId ?? null;
  } catch {
    return null;
  }
}

function Installation({
  status,
  host,
  onChange,
}: {
  status: Status;
  host: string;
  onChange: () => void;
}) {
  const { t } = useTranslation('installer');
  const [stage, setStage] = useState<'key' | 'stick'>('key');
  const reboot = useMutation({
    mutationFn: async () => {
      unwrap(await api.POST('/api/installer/reboot'));
    },
  });
  const rebooting = status.phase === 'rebooting' || reboot.isSuccess;
  const probe = useQuery({
    queryKey: ['installer', 'probe'],
    queryFn: ({ signal }) => probeInstalledSystem(host, signal),
    enabled: rebooting,
    refetchInterval: 3000,
    retry: false,
  });
  const installed = rebooting && probe.data != null && probe.data === status.installId;
  // The installed box speaks HTTPS on 7484 with its own certificate (D346).
  const target = `https://${host}:7484/`;

  // Leaving the page for the installed system is a side effect of the probe's answer.
  useEffect(() => {
    if (installed) {
      location.assign(target);
    }
  }, [installed, target]);

  if (status.phase === 'installing') {
    return (
      <Message title={t('progress.title')} intro={t('progress.intro')}>
        <ol className={own.list} aria-live="polite">
          {status.stepCount > 0 &&
            (
              [
                'partition',
                'raid',
                'filesystems',
                'copy',
                'configure',
                'bootloader',
                'finish',
              ] as const
            ).map((step, index) => (
              <li
                key={step}
                className={own.check}
                data-current={index === status.stepIndex || undefined}
                aria-current={index === status.stepIndex ? 'step' : undefined}
              >
                {index < status.stepIndex ? (
                  <IconCheck size={18} stroke={2} aria-hidden className={own.ok} />
                ) : index === status.stepIndex ? (
                  <IconLoader2 size={18} stroke={2} aria-hidden className={own.spinner} />
                ) : (
                  <span className={own.pending} aria-hidden />
                )}
                <span>{t(`progress.steps.${step}`)}</span>
              </li>
            ))}
        </ol>
      </Message>
    );
  }
  if (status.phase === 'failed') {
    const step = status.step ?? 'partition';
    return (
      <Message
        title={t('failed.title')}
        intro={t('failed.step', {
          step: t(`progress.steps.${step}` as 'progress.steps.partition'),
        })}
      >
        {typeof status.errorParams.command === 'string' && (
          <p className={own.hint}>
            {t('failed.command', {
              command: status.errorParams.command,
              exitCode: String(status.errorParams.exitCode),
            })}
          </p>
        )}
        {status.errorCode != null && (
          <ErrorNotice error={{ code: status.errorCode as ErrorCode, params: {}, requestId: '' }} />
        )}
        <Button variant="primary" className={styles.submit} onClick={onChange}>
          {t('failed.change')}
        </Button>
      </Message>
    );
  }
  if (rebooting) {
    return (
      <Message title={t('rebooting.title')} intro={t('rebooting.intro')}>
        <p className={own.hint}>{t('rebooting.certificate')}</p>
        <p className={own.hint}>{t('rebooting.manual', { url: target })}</p>
      </Message>
    );
  }
  if (stage === 'key' && status.recoveryKey != null) {
    return (
      <RecoveryKeyCard
        recoveryKey={status.recoveryKey}
        title={t('done.title')}
        intro={t('done.recoveryIntro')}
        continueLabel={t('done.continue')}
        onContinue={() => {
          setStage('stick');
        }}
      />
    );
  }
  return (
    <Message title={t('reboot.title')} intro={t('reboot.intro')}>
      {reboot.isError && <ErrorNotice error={describeError(reboot.error)} />}
      <Button
        variant="primary"
        className={styles.submit}
        pending={reboot.isPending}
        autoFocus
        onClick={() => {
          reboot.mutate();
        }}
      >
        {t('reboot.restart')}
      </Button>
    </Message>
  );
}
