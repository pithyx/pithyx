import type { PithyxApp } from '@pithyx/sdk';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { languageName, languages, useFormatLocale } from '../../i18n';
import { REGIONS } from '../../regions';
import { Button } from '../../ui/Button';
import { TextField } from '../../ui/TextField';
import { systemCall, type SystemRegion, type TimeSettings } from '../sdk/system';
import { describeProblem } from '../storage/shared';
import styles from './Settings.module.css';

const timeKey = ['settings', 'system', 'time'] as const;
const regionKey = ['settings', 'system', 'region'] as const;

/**
 * Regional settings of the box: the system language, the region whose date, time and number formats everyone sees,
 * the time zone and the time servers. Each user's own language is a personal setting in the user menu.
 */
export function RegionalPage({ app }: { app: PithyxApp }) {
  const { t } = useTranslation('settings');
  const region = useQuery({
    queryKey: regionKey,
    queryFn: () => systemCall(app, 'internal.admin.system.region', {}),
  });
  const time = useQuery({
    queryKey: timeKey,
    queryFn: () => systemCall(app, 'internal.admin.system.time', {}),
  });
  return (
    <div className={styles.content}>
      <header className={styles.pageHeader}>
        <div>
          <h2 className={styles.pageTitle}>{t('pages.regional.title')}</h2>
          <p className={styles.muted}>{t('regional.intro')}</p>
        </div>
      </header>
      {region.isError && <ErrorNotice error={describeProblem(region.error)} />}
      {region.data !== undefined && <RegionForm app={app} saved={region.data} />}
      {time.isError && <ErrorNotice error={describeProblem(time.error)} />}
      {time.data !== undefined && <TimeForm app={app} settings={time.data} />}
    </div>
  );
}

function RegionForm({ app, saved }: { app: PithyxApp; saved: SystemRegion }) {
  const { t, i18n } = useTranslation('settings');
  // The selects name themselves by their label only; a wrapping label would add the selected option and the hint.
  const ids = useId();
  const queryClient = useQueryClient();
  const [language, setLanguage] = useState(saved.language);
  const [region, setRegion] = useState(saved.region ?? '');
  const save = useMutation({
    mutationFn: () =>
      systemCall(app, 'internal.admin.system.setRegion', {
        language,
        region: region === '' ? null : region,
      }),
    onSuccess: (next) => {
      queryClient.setQueryData(regionKey, next);
    },
  });
  const countries = new Intl.DisplayNames([i18n.language], { type: 'region' });
  const regions = REGIONS.map((code) => ({ code, name: countries.of(code) ?? code })).sort((a, b) =>
    a.name.localeCompare(b.name, i18n.language),
  );
  const sample = new Date(Date.UTC(2026, 11, 31, 16, 5));
  const previewLocale = region === '' ? i18n.language : `${i18n.language}-${region}`;
  const preview = [
    new Intl.DateTimeFormat(previewLocale, { dateStyle: 'medium', timeZone: 'UTC' }).format(sample),
    new Intl.DateTimeFormat(previewLocale, { dateStyle: 'short', timeZone: 'UTC' }).format(sample),
    new Intl.DateTimeFormat(previewLocale, { timeStyle: 'short', timeZone: 'UTC' }).format(sample),
    new Intl.NumberFormat(previewLocale).format(1234567.89),
  ].join(' · ');
  const changed = language !== saved.language || region !== (saved.region ?? '');

  return (
    <form
      className={styles.form}
      aria-labelledby="regional-language"
      onSubmit={(event) => {
        event.preventDefault();
        save.mutate();
      }}
    >
      <h3 id="regional-language" className={styles.sectionTitle}>
        {t('regional.languageTitle')}
      </h3>
      <label className={styles.selectField}>
        <span id={`${ids}-language`} className={styles.fieldLabel}>
          {t('regional.language')}
        </span>
        <select
          aria-labelledby={`${ids}-language`}
          aria-describedby={`${ids}-language-hint`}
          className={styles.select}
          value={language}
          onChange={(event) => {
            setLanguage(event.target.value);
            save.reset();
          }}
        >
          {languages.map((code) => (
            <option key={code} value={code} lang={code}>
              {languageName(code)}
            </option>
          ))}
        </select>
        <span id={`${ids}-language-hint`} className={styles.optionHint}>
          {t('regional.languageHint')}
        </span>
      </label>
      <label className={styles.selectField}>
        <span id={`${ids}-formats`} className={styles.fieldLabel}>
          {t('regional.formats')}
        </span>
        <select
          aria-labelledby={`${ids}-formats`}
          aria-describedby={`${ids}-formats-hint`}
          className={styles.select}
          value={region}
          onChange={(event) => {
            setRegion(event.target.value);
            save.reset();
          }}
        >
          <option value="">{t('regional.formatsLanguage')}</option>
          {regions.map((entry) => (
            <option key={entry.code} value={entry.code}>
              {entry.name}
            </option>
          ))}
        </select>
        <span id={`${ids}-formats-hint`} className={styles.optionHint}>
          {t('regional.formatsHint')}
        </span>
      </label>
      <p className={styles.muted}>{t('regional.preview', { preview })}</p>
      {save.isError && <ErrorNotice error={describeProblem(save.error)} />}
      {save.isSuccess && (
        <p className={styles.muted} role="status">
          {t('regional.saved')}
        </p>
      )}
      <div>
        <Button type="submit" variant="primary" isDisabled={!changed || save.isPending}>
          {t('regional.saveLanguage')}
        </Button>
      </div>
    </form>
  );
}

function TimeForm({ app, settings }: { app: PithyxApp; settings: TimeSettings }) {
  const { t } = useTranslation('settings');
  const zoneLabel = useId();
  const locale = useFormatLocale();
  const queryClient = useQueryClient();
  const [zone, setZone] = useState(settings.timeZone);
  const [servers, setServers] = useState(settings.ntpServers.join(' '));
  const save = useMutation({
    mutationFn: () =>
      systemCall(app, 'internal.admin.system.setTime', {
        timeZone: zone,
        ntpServers: servers.split(/[\s,]+/).filter((server) => server !== ''),
      }),
    onSuccess: (saved) => {
      queryClient.setQueryData(timeKey, saved);
    },
  });
  const now = new Intl.DateTimeFormat(locale, {
    dateStyle: 'full',
    timeStyle: 'short',
    timeZone: settings.timeZone,
  }).format(new Date());
  const changed = zone !== settings.timeZone || servers.trim() !== settings.ntpServers.join(' ');

  return (
    <form
      className={styles.form}
      aria-labelledby="regional-time"
      onSubmit={(event) => {
        event.preventDefault();
        save.mutate();
      }}
    >
      <h3 id="regional-time" className={styles.sectionTitle}>
        {t('regional.timeTitle')}
      </h3>
      <dl className={styles.facts}>
        <div className={styles.fact}>
          <dt>{t('regional.now')}</dt>
          <dd>{now}</dd>
        </div>
        <div className={styles.fact}>
          <dt>{t('regional.sync')}</dt>
          <dd>
            {settings.synchronized ? t('regional.synchronized') : t('regional.notSynchronized')}
          </dd>
        </div>
      </dl>
      <label className={styles.selectField}>
        <span id={zoneLabel} className={styles.fieldLabel}>
          {t('regional.zone')}
        </span>
        <select
          aria-labelledby={zoneLabel}
          className={styles.select}
          value={zone}
          onChange={(event) => {
            setZone(event.target.value);
            save.reset();
          }}
        >
          {settings.timeZones.map((name) => (
            <option key={name} value={name}>
              {name.replaceAll('_', ' ')}
            </option>
          ))}
        </select>
      </label>
      <TextField
        label={t('regional.servers')}
        description={t('regional.serversHint')}
        value={servers}
        onChange={(value) => {
          setServers(value);
          save.reset();
        }}
        autoCapitalize="none"
        autoComplete="off"
        spellCheck="false"
      />
      {save.isError && <ErrorNotice error={describeProblem(save.error)} />}
      {save.isSuccess && (
        <p className={styles.muted} role="status">
          {t('regional.saved')}
        </p>
      )}
      <div>
        <Button type="submit" variant="primary" isDisabled={!changed || save.isPending}>
          {t('regional.saveTime')}
        </Button>
      </div>
    </form>
  );
}
