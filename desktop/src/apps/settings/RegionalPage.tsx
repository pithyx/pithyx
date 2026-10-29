import type { PithyxApp } from '@pithyx/sdk';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { languageName, languages, useFormatLocale } from '../../i18n';
import { REGIONS } from '../../regions';
import { ActionBar, FormRow, FormSection, Select, TextField } from '@pithyx/ui';
import { systemCall, type SystemRegion, type TimeSettings } from '../sdk/system';
import { describeProblem } from '../storage/shared';
import { PageFooter } from './footer';
import styles from './Settings.module.css';

const timeKey = ['settings', 'system', 'time'] as const;
const regionKey = ['settings', 'system', 'region'] as const;

/**
 * Regional settings of the box (D394): the system language and the region whose date, time and number formats everyone
 * sees, then the time zone and the time servers, applied together from the action bar. Each user's own language is a
 * personal setting.
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
      <p className={styles.muted}>{t('regional.intro')}</p>
      {region.isError && <ErrorNotice error={describeProblem(region.error)} />}
      {time.isError && <ErrorNotice error={describeProblem(time.error)} />}
      {region.data !== undefined && time.data !== undefined && (
        <RegionalForm app={app} savedRegion={region.data} savedTime={time.data} />
      )}
    </div>
  );
}

function RegionalForm({
  app,
  savedRegion,
  savedTime,
}: {
  app: PithyxApp;
  savedRegion: SystemRegion;
  savedTime: TimeSettings;
}) {
  const { t, i18n } = useTranslation('settings');
  const locale = useFormatLocale();
  const queryClient = useQueryClient();
  const [language, setLanguage] = useState(savedRegion.language);
  const [region, setRegion] = useState(savedRegion.region ?? '');
  const [zone, setZone] = useState(savedTime.timeZone);
  const [servers, setServers] = useState(savedTime.ntpServers.join(' '));
  const regionChanged = language !== savedRegion.language || region !== (savedRegion.region ?? '');
  const timeChanged =
    zone !== savedTime.timeZone || servers.trim() !== savedTime.ntpServers.join(' ');
  const save = useMutation({
    mutationFn: async () => {
      if (regionChanged) {
        queryClient.setQueryData(
          regionKey,
          await systemCall(app, 'internal.admin.system.setRegion', {
            language,
            region: region === '' ? null : region,
          }),
        );
      }
      if (timeChanged) {
        queryClient.setQueryData(
          timeKey,
          await systemCall(app, 'internal.admin.system.setTime', {
            timeZone: zone,
            ntpServers: servers.split(/[\s,]+/).filter((server) => server !== ''),
          }),
        );
      }
    },
  });
  const edit =
    <T,>(set: (value: T) => void) =>
    (value: T) => {
      set(value);
      save.reset();
    };

  const countries = new Intl.DisplayNames([i18n.language], { type: 'region' });
  const regions = REGIONS.map((code) => ({ id: code, label: countries.of(code) ?? code })).sort(
    (a, b) => a.label.localeCompare(b.label, i18n.language),
  );
  const sample = new Date(Date.UTC(2026, 11, 31, 16, 5));
  const previewLocale = region === '' ? i18n.language : `${i18n.language}-${region}`;
  const preview = [
    new Intl.DateTimeFormat(previewLocale, { dateStyle: 'medium', timeZone: 'UTC' }).format(sample),
    new Intl.DateTimeFormat(previewLocale, { dateStyle: 'short', timeZone: 'UTC' }).format(sample),
    new Intl.DateTimeFormat(previewLocale, { timeStyle: 'short', timeZone: 'UTC' }).format(sample),
    new Intl.NumberFormat(previewLocale).format(1234567.89),
  ].join(' · ');
  const now = new Intl.DateTimeFormat(locale, {
    dateStyle: 'full',
    timeStyle: 'short',
    timeZone: savedTime.timeZone,
  }).format(new Date());

  return (
    <>
      <FormSection title={t('regional.languageTitle')}>
        <FormRow label={t('regional.language')} hint={t('regional.languageHint')}>
          <Select
            options={languages.map((code) => ({ id: code, label: languageName(code) }))}
            value={language}
            onChange={(value) => {
              edit(setLanguage)(String(value));
            }}
          />
        </FormRow>
        <FormRow label={t('regional.formats')} hint={t('regional.formatsHint')}>
          <Select
            options={[{ id: '', label: t('regional.formatsLanguage') }, ...regions]}
            value={region}
            onChange={(value) => {
              edit(setRegion)(String(value));
            }}
          />
        </FormRow>
        <FormRow label={t('regional.example')}>
          <span className={styles.muted}>{preview}</span>
        </FormRow>
      </FormSection>

      <FormSection title={t('regional.timeTitle')}>
        <FormRow label={t('regional.now')}>{now}</FormRow>
        <FormRow label={t('regional.sync')}>
          {savedTime.synchronized ? t('regional.synchronized') : t('regional.notSynchronized')}
        </FormRow>
        <FormRow label={t('regional.zone')}>
          <Select
            options={savedTime.timeZones.map((name) => ({
              id: name,
              label: name.replaceAll('_', ' '),
            }))}
            value={zone}
            onChange={(value) => {
              edit(setZone)(String(value));
            }}
          />
        </FormRow>
        <FormRow label={t('regional.servers')} hint={t('regional.serversHint')}>
          <TextField
            value={servers}
            onChange={edit(setServers)}
            autoCapitalize="none"
            autoComplete="off"
            spellCheck="false"
          />
        </FormRow>
      </FormSection>

      {save.isError && <ErrorNotice error={describeProblem(save.error)} />}
      {save.isSuccess && (
        <p className={styles.muted} role="status">
          {t('regional.saved')}
        </p>
      )}
      <PageFooter>
        <ActionBar
          isDirty={regionChanged || timeChanged}
          isPending={save.isPending}
          message={t('regional.applyNote')}
          onApply={() => {
            save.mutate();
          }}
          onDiscard={() => {
            setLanguage(savedRegion.language);
            setRegion(savedRegion.region ?? '');
            setZone(savedTime.timeZone);
            setServers(savedTime.ntpServers.join(' '));
            save.reset();
          }}
        />
      </PageFooter>
    </>
  );
}
