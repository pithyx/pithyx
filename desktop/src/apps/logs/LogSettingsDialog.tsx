import type { PithyxApp } from '@pithyx/sdk';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState, type SyntheticEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { formatBytes } from '../../desktop/format';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { Button } from '../../ui/Button';
import { Switch } from '../../ui/Controls';
import { Dialog } from '../../ui/Dialog';
import { describeProblem, logsCall, type LogSettings, type LogStatus } from '../sdk/logs';
import styles from './Logs.module.css';

const GB = 1024 ** 3;
const FIRST_POOL = '';

/** Whole gigabytes for the size fields; the daemon takes bytes. */
function gigabytes(bytes: number): string {
  return String(Math.round((bytes / GB) * 100) / 100);
}

function parseNumber(text: string): number {
  return Number(text.trim().replace(',', '.'));
}

/** Retention of the current logs and of the optional archive on a pool, with what they take now. */
export function LogSettingsDialog({ app, onClose }: { app: PithyxApp; onClose: () => void }) {
  const { t } = useTranslation('logs');
  const status = useQuery({
    queryKey: ['logs', 'settings'],
    queryFn: () => logsCall(app, 'internal.admin.logs.settings', {}),
  });
  return (
    <Dialog
      isOpen
      isDismissable
      onOpenChange={(open) => {
        if (!open) {
          onClose();
        }
      }}
      title={t('settings.title')}
      actions={
        <>
          <Button variant="secondary" onPress={onClose}>
            {t('settings.cancel')}
          </Button>
          <Button
            variant="primary"
            type="submit"
            form="log-settings"
            isDisabled={status.data === undefined}
          >
            {t('settings.save')}
          </Button>
        </>
      }
    >
      {status.isError && <ErrorNotice error={describeProblem(status.error)} />}
      {status.data !== undefined && (
        <SettingsForm app={app} status={status.data} onSaved={onClose} />
      )}
    </Dialog>
  );
}

function SettingsForm({
  app,
  status,
  onSaved,
}: {
  app: PithyxApp;
  status: LogStatus;
  onSaved: () => void;
}) {
  const { t, i18n } = useTranslation('logs');
  const client = useQueryClient();
  const { settings } = status;
  const [retentionDays, setRetentionDays] = useState(String(settings.retentionDays));
  const [maxGb, setMaxGb] = useState(gigabytes(settings.maxBytes));
  const [archiveEnabled, setArchiveEnabled] = useState(settings.archiveEnabled);
  const [archivePool, setArchivePool] = useState(settings.archivePool ?? FIRST_POOL);
  const [archiveDays, setArchiveDays] = useState(String(settings.archiveRetentionDays));
  const [archiveGb, setArchiveGb] = useState(gigabytes(settings.archiveMaxBytes));
  const save = useMutation({
    mutationFn: (next: LogSettings) => logsCall(app, 'internal.admin.logs.setSettings', next),
    onSuccess: (saved) => {
      client.setQueryData(['logs', 'settings'], saved);
      onSaved();
    },
  });
  const size = (bytes: number | null | undefined) => formatBytes(bytes ?? 0, i18n.language);
  const date = (value: string | null | undefined) =>
    value === null || value === undefined
      ? t('settings.never')
      : new Intl.DateTimeFormat(i18n.language, { dateStyle: 'medium', timeStyle: 'short' }).format(
          new Date(value),
        );

  function submit(event: SyntheticEvent<HTMLFormElement>) {
    event.preventDefault();
    save.mutate({
      retentionDays: Math.round(parseNumber(retentionDays)),
      maxBytes: Math.round(parseNumber(maxGb) * GB),
      archiveEnabled,
      archivePool: archivePool === FIRST_POOL ? null : archivePool,
      archiveRetentionDays: Math.round(parseNumber(archiveDays)),
      archiveMaxBytes: Math.round(parseNumber(archiveGb) * GB),
    });
  }

  const number = (
    label: string,
    value: string,
    onChange: (value: string) => void,
    hint: string,
  ) => (
    <label className={styles.formField}>
      <span className={styles.fieldLabel}>{label}</span>
      <input
        className={styles.input}
        inputMode="decimal"
        value={value}
        onChange={(event) => {
          onChange(event.target.value);
        }}
      />
      <span className={styles.muted}>{hint}</span>
    </label>
  );

  return (
    <form id="log-settings" className={styles.form} onSubmit={submit}>
      <p className={styles.muted}>{t('settings.intro')}</p>
      <fieldset className={styles.group}>
        <legend className={styles.groupTitle}>{t('settings.current')}</legend>
        <p className={styles.muted}>
          {t('settings.currentStatus', {
            size: size(status.currentBytes),
            oldest: date(status.currentOldest),
          })}
        </p>
        {number(
          t('settings.retentionDays'),
          retentionDays,
          setRetentionDays,
          t('settings.retentionHint'),
        )}
        {number(t('settings.maxGb'), maxGb, setMaxGb, t('settings.maxHint'))}
      </fieldset>
      <fieldset className={styles.group}>
        <legend className={styles.groupTitle}>{t('settings.archive')}</legend>
        <p className={styles.muted}>{t('settings.archiveIntro')}</p>
        <Switch
          isSelected={archiveEnabled}
          onChange={setArchiveEnabled}
          isDisabled={status.pools.length === 0}
        >
          {t('settings.archiveEnabled')}
        </Switch>
        {status.pools.length === 0 && <p className={styles.muted}>{t('settings.noPool')}</p>}
        {archiveEnabled && (
          <>
            <label className={styles.formField}>
              <span className={styles.fieldLabel}>{t('settings.pool')}</span>
              <select
                className={styles.select}
                value={archivePool}
                onChange={(event) => {
                  setArchivePool(event.target.value);
                }}
              >
                <option value={FIRST_POOL}>{t('settings.firstPool')}</option>
                {status.pools.map((pool) => (
                  <option key={pool} value={pool}>
                    {pool}
                  </option>
                ))}
              </select>
            </label>
            {number(
              t('settings.archiveDays'),
              archiveDays,
              setArchiveDays,
              t('settings.archiveDaysHint'),
            )}
            {number(t('settings.archiveGb'), archiveGb, setArchiveGb, t('settings.archiveGbHint'))}
            <p className={styles.muted}>
              {status.archiveState === 'unavailable'
                ? t('settings.archiveUnavailable', { pool: status.archivePool ?? '' })
                : status.archiveState === 'ready'
                  ? t('settings.archiveStatus', {
                      pool: status.archivePool ?? '',
                      size: size(status.archiveBytes),
                      copied: date(status.archivedAt),
                    })
                  : t('settings.archiveOnSave')}
            </p>
          </>
        )}
      </fieldset>
      {save.isError && <ErrorNotice error={describeProblem(save.error)} />}
    </form>
  );
}
