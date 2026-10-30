import type { PithyxApp } from '@pithyx/sdk';
import {
  ActionBar,
  FormRow,
  FormSection,
  PasswordField,
  Radio,
  RadioGroup,
  StatusBadge,
  TextField,
} from '@pithyx/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { useFormatLocale } from '../../i18n';
import { onUps, upsCall, upsKey, type UpsMode, type UpsStatus } from '../sdk/ups';
import { describeProblem } from '../storage/shared';
import { PageFooter } from './footer';
import styles from './Settings.module.css';

/**
 * Settings > UPS (D377): a UPS on USB or one of another NUT server, what it reports right now, and the limits at which
 * the box shuts down on battery. The state follows the topic `system/ups` while the page is open.
 */
export function UpsPage({ app }: { app: PithyxApp }) {
  const { t } = useTranslation('settings');
  const queryClient = useQueryClient();
  const status = useQuery({
    queryKey: upsKey,
    queryFn: () => upsCall(app, 'internal.admin.ups.get', {}),
  });
  // The topic is outside React; every change replaces the cached answer.
  useEffect(
    () =>
      onUps(app, (next) => {
        queryClient.setQueryData(upsKey, next);
      }),
    [app, queryClient],
  );
  return (
    <div className={styles.content}>
      <p className={styles.sectionIntro}>{t('ups.intro')}</p>
      {status.isError && <ErrorNotice error={describeProblem(status.error)} />}
      {status.data !== undefined && (
        <>
          <UpsState status={status.data} />
          <UpsForm app={app} saved={status.data} />
        </>
      )}
    </div>
  );
}

function UpsState({ status }: { status: UpsStatus }) {
  const { t } = useTranslation('settings');
  const locale = useFormatLocale();
  const { state } = status;
  if (state.connection === 'off') {
    return null;
  }
  const number = new Intl.NumberFormat(locale);
  const rows: [string, string][] = [
    [
      t('ups.power'),
      state.power === 'onBattery' && state.onBatterySince != null
        ? t('ups.onBatterySince', {
            time: new Intl.DateTimeFormat(locale, { timeStyle: 'short' }).format(
              new Date(state.onBatterySince),
            ),
          })
        : t(`ups.powerState.${state.power}`),
    ],
  ];
  if (state.charge != null) {
    rows.push([t('ups.charge'), `${number.format(state.charge)} %`]);
  }
  if (state.runtime != null) {
    rows.push([t('ups.runtime'), t('ups.minutes', { count: Math.floor(state.runtime / 60) })]);
  }
  if (state.load != null) {
    rows.push([t('ups.load'), `${number.format(state.load)} %`]);
  }
  if (state.model != null) {
    rows.push([t('ups.model'), state.model]);
  }
  const tone =
    state.connection === 'connected'
      ? state.power === 'onBattery'
        ? 'warning'
        : 'success'
      : state.connection === 'unreachable'
        ? 'danger'
        : 'neutral';
  return (
    <FormSection
      title={t('ups.stateTitle')}
      end={<StatusBadge tone={tone}>{t(`ups.connection.${state.connection}`)}</StatusBadge>}
    >
      {state.shutdownReason != null && (
        <div className={styles.notice} role="alert">
          {t(`ups.shutdown.${known(REASONS, state.shutdownReason, 'forced')}`)}
        </div>
      )}
      {state.connection === 'unreachable' && state.problem != null && (
        <div className={styles.notice} role="status">
          {t(`ups.problem.${known(PROBLEMS, state.problem, 'protocol')}`)}
        </div>
      )}
      {state.connection !== 'connecting' &&
        rows.map(([label, value]) => (
          <FormRow key={label} label={label}>
            <span>{value}</span>
          </FormRow>
        ))}
    </FormSection>
  );
}

const PROBLEMS = [
  'refused',
  'timeout',
  'unknown_ups',
  'driver_not_connected',
  'data_stale',
  'access_denied',
  'protocol',
] as const;
const REASONS = ['forced', 'low_battery', 'charge', 'runtime', 'lost'] as const;

/** The known word of a daemon value, or the fallback for one a newer daemon added. */
const known = <T extends string>(words: readonly T[], value: string, fallback: T): T =>
  words.find((word) => word === value) ?? fallback;

const parse = (text: string) => (/^\d+$/.test(text.trim()) ? Number(text.trim()) : Number.NaN);

function UpsForm({ app, saved }: { app: PithyxApp; saved: UpsStatus }) {
  const { t } = useTranslation('settings');
  const queryClient = useQueryClient();
  const settings = saved.settings;
  const [mode, setMode] = useState<UpsMode>(settings.mode);
  const [host, setHost] = useState(settings.host);
  const [port, setPort] = useState(String(settings.port));
  const [name, setName] = useState(settings.name);
  const [username, setUsername] = useState(settings.username);
  const [password, setPassword] = useState('');
  const [batteryLow, setBatteryLow] = useState(String(settings.batteryLow));
  const [runtimeLow, setRuntimeLow] = useState(String(settings.runtimeLow));
  const save = useMutation({
    mutationFn: () =>
      upsCall(app, 'internal.admin.ups.set', {
        mode,
        host,
        port: parse(port),
        name,
        username,
        // Empty keeps the saved password.
        ...(password === '' ? {} : { password }),
        batteryLow: parse(batteryLow),
        runtimeLow: parse(runtimeLow),
      }),
    onSuccess: (next) => {
      queryClient.setQueryData(upsKey, next);
      setPassword('');
    },
  });
  const numbersValid = [port, batteryLow, runtimeLow].every((text) => !Number.isNaN(parse(text)));

  const changed =
    mode !== settings.mode ||
    host !== settings.host ||
    port !== String(settings.port) ||
    name !== settings.name ||
    username !== settings.username ||
    password !== '' ||
    batteryLow !== String(settings.batteryLow) ||
    runtimeLow !== String(settings.runtimeLow);
  const discard = () => {
    setMode(settings.mode);
    setHost(settings.host);
    setPort(String(settings.port));
    setName(settings.name);
    setUsername(settings.username);
    setPassword('');
    setBatteryLow(String(settings.batteryLow));
    setRuntimeLow(String(settings.runtimeLow));
    save.reset();
  };

  return (
    <>
      <FormSection title={t('ups.settingsTitle')}>
        <FormRow label={t('ups.mode')} hint={t(`ups.modeHint.${mode}`)}>
          <RadioGroup
            orientation="vertical"
            value={mode}
            onChange={(value) => {
              const next = MODES.find((entry) => entry === value);
              if (next !== undefined) {
                setMode(next);
              }
            }}
          >
            {MODES.map((entry) => (
              <Radio key={entry} value={entry} isDisabled={entry === 'usb' && !saved.usbAvailable}>
                {t(`ups.modes.${entry}`)}
              </Radio>
            ))}
          </RadioGroup>
        </FormRow>
        {mode === 'network' && (
          <>
            <FormRow label={t('ups.host')} hint={t('ups.hostHint')}>
              <TextField
                value={host}
                onChange={setHost}
                autoComplete="off"
                autoCapitalize="none"
                isRequired
              />
            </FormRow>
            <FormRow label={t('ups.port')}>
              <TextField value={port} onChange={setPort} inputMode="numeric" autoComplete="off" />
            </FormRow>
            <FormRow label={t('ups.name')} hint={t('ups.nameHint')}>
              <TextField value={name} onChange={setName} autoComplete="off" autoCapitalize="none" />
            </FormRow>
            <FormRow label={t('ups.username')} hint={t('ups.usernameHint')}>
              <TextField
                value={username}
                onChange={setUsername}
                autoComplete="off"
                autoCapitalize="none"
              />
            </FormRow>
            <FormRow
              label={t('ups.password')}
              {...(settings.hasPassword ? { hint: t('ups.passwordKept') } : {})}
            >
              <PasswordField value={password} onChange={setPassword} autoComplete="new-password" />
            </FormRow>
          </>
        )}
      </FormSection>
      {mode !== 'off' && (
        <FormSection title={t('ups.limitsTitle')}>
          <p className={styles.sectionIntro}>{t('ups.limits')}</p>
          <FormRow label={t('ups.batteryLow')} hint={t('ups.batteryLowHint')}>
            <TextField
              value={batteryLow}
              onChange={setBatteryLow}
              inputMode="numeric"
              autoComplete="off"
            />
          </FormRow>
          <FormRow label={t('ups.runtimeLow')} hint={t('ups.runtimeLowHint')}>
            <TextField
              value={runtimeLow}
              onChange={setRuntimeLow}
              inputMode="numeric"
              autoComplete="off"
            />
          </FormRow>
        </FormSection>
      )}
      {save.isError && <ErrorNotice error={describeProblem(save.error)} />}
      <PageFooter>
        <ActionBar
          isDirty={changed && numbersValid}
          isPending={save.isPending}
          message={t('ups.applyNote')}
          onApply={() => {
            save.mutate();
          }}
          onDiscard={discard}
        />
      </PageFooter>
    </>
  );
}

const MODES = ['off', 'usb', 'network'] as const satisfies readonly UpsMode[];
