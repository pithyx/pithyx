import type { PithyxApp } from '@pithyx/sdk';
import { Badge, Button, PasswordField, TextField } from '@pithyx/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { useFormatLocale } from '../../i18n';
import { onUps, upsCall, upsKey, type UpsMode, type UpsStatus } from '../sdk/ups';
import { describeProblem } from '../storage/shared';
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
      <header className={styles.pageHeader}>
        <div>
          <h2 className={styles.pageTitle}>{t('pages.ups.title')}</h2>
          <p className={styles.muted}>{t('ups.intro')}</p>
        </div>
      </header>
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
  return (
    <section className={styles.card} aria-labelledby="ups-state">
      <h3 id="ups-state" className={styles.sectionTitle}>
        {t('ups.stateTitle')} <Badge>{t(`ups.connection.${state.connection}`)}</Badge>
      </h3>
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
      {state.connection !== 'connecting' && (
        <dl className={styles.facts}>
          {rows.map(([label, value]) => (
            <div key={label} className={styles.fact}>
              <dt>{label}</dt>
              <dd>{value}</dd>
            </div>
          ))}
        </dl>
      )}
    </section>
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
  const id = useId();
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

  return (
    <form
      className={styles.card}
      aria-labelledby={`${id}-title`}
      onSubmit={(event) => {
        event.preventDefault();
        save.mutate();
      }}
    >
      <h3 id={`${id}-title`} className={styles.sectionTitle}>
        {t('ups.settingsTitle')}
      </h3>
      {save.isError && <ErrorNotice error={describeProblem(save.error)} />}
      <label className={styles.selectField}>
        <span id={`${id}-mode`} className={styles.fieldLabel}>
          {t('ups.mode')}
        </span>
        <select
          aria-labelledby={`${id}-mode`}
          className={styles.select}
          value={mode}
          onChange={(event) => {
            setMode(event.target.value as UpsMode);
          }}
        >
          <option value="off">{t('ups.modes.off')}</option>
          <option value="usb" disabled={!saved.usbAvailable}>
            {t('ups.modes.usb')}
          </option>
          <option value="network">{t('ups.modes.network')}</option>
        </select>
      </label>
      <p className={styles.muted}>{t(`ups.modeHint.${mode}`)}</p>
      {mode === 'network' && (
        <div className={styles.group}>
          <TextField
            label={t('ups.host')}
            description={t('ups.hostHint')}
            value={host}
            onChange={setHost}
            autoComplete="off"
            autoCapitalize="none"
            isRequired
          />
          <TextField
            label={t('ups.port')}
            value={port}
            onChange={setPort}
            inputMode="numeric"
            autoComplete="off"
          />
          <TextField
            label={t('ups.name')}
            description={t('ups.nameHint')}
            value={name}
            onChange={setName}
            autoComplete="off"
            autoCapitalize="none"
          />
          <TextField
            label={t('ups.username')}
            description={t('ups.usernameHint')}
            value={username}
            onChange={setUsername}
            autoComplete="off"
            autoCapitalize="none"
          />
          <PasswordField
            label={t('ups.password')}
            description={settings.hasPassword ? t('ups.passwordKept') : undefined}
            value={password}
            onChange={setPassword}
            autoComplete="new-password"
          />
        </div>
      )}
      {mode !== 'off' && (
        <div className={styles.group}>
          <p className={styles.muted}>{t('ups.limits')}</p>
          <TextField
            label={t('ups.batteryLow')}
            description={t('ups.batteryLowHint')}
            value={batteryLow}
            onChange={setBatteryLow}
            inputMode="numeric"
            autoComplete="off"
          />
          <TextField
            label={t('ups.runtimeLow')}
            description={t('ups.runtimeLowHint')}
            value={runtimeLow}
            onChange={setRuntimeLow}
            inputMode="numeric"
            autoComplete="off"
          />
        </div>
      )}
      <div className={styles.buttons}>
        <Button type="submit" variant="primary" isDisabled={!numbersValid || save.isPending}>
          {t('ups.save')}
        </Button>
      </div>
    </form>
  );
}
