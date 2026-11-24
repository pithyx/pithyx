import type { PithyxApp } from '@pithyx/sdk';
import { Button, Checkbox, Dialog, PasswordField, Select, Switch, TextField } from '@pithyx/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useId, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { copyText } from '../../platform/browser';
import { IconCopy, IconDownload, IconPlus, IconTrash } from '../../ui/icons';
import {
  backupsCall,
  jobsKey,
  sourcesKey,
  type BackupJob,
  type BackupJobSettings,
  type BackupRetention,
  type BackupTarget,
} from '../sdk/backups';
import { describeProblem } from '../storage/shared';
import { cronOf, scheduleOf, switchKind, type Schedule } from './schedule';
import styles from './Backup.module.css';

export const RETENTION_KEYS = ['last', 'hourly', 'daily', 'weekly', 'monthly', 'yearly'] as const;
export type RetentionKey = (typeof RETENTION_KEYS)[number];

const SCHEDULE_KINDS = ['hourly', 'daily', 'weekly', 'monthly', 'custom'] as const;
const WEEKDAYS = [1, 2, 3, 4, 5, 6, 0] as const;

/** A new job's: every night, checked every Sunday morning reading 5 percent, a week, a month and a year of versions. */
export const NEW_SCHEDULE: Schedule = { kind: 'daily', time: '02:00' };
const NEW_CHECK: Schedule = { kind: 'weekly', day: 0, time: '05:00' };
export const NEW_RETENTION: Record<RetentionKey, string> = {
  last: '0',
  hourly: '0',
  daily: '7',
  weekly: '4',
  monthly: '12',
  yearly: '0',
};

interface SourceDraft {
  shareId: number | null;
  path: string;
}

/** What the administrator edits; numbers as typed. */
interface Draft {
  name: string;
  targetId: number | null;
  repository: string;
  repositoryTouched: boolean;
  existing: boolean;
  existingKey: string;
  sources: SourceDraft[];
  schedule: Schedule;
  enabled: boolean;
  retention: Record<RetentionKey, string>;
  check: Schedule | null;
  checkReadPercent: string;
  uploadLimitKib: string;
  downloadLimitKib: string;
  overdueHours: string;
  excludes: string;
}

function draftOf(job: BackupJob | undefined, targets: readonly BackupTarget[]): Draft {
  if (job === undefined) {
    return {
      name: '',
      targetId: targets.length === 1 ? (targets[0]?.id ?? null) : null,
      repository: '',
      repositoryTouched: false,
      existing: false,
      existingKey: '',
      sources: [{ shareId: null, path: '' }],
      schedule: NEW_SCHEDULE,
      enabled: true,
      retention: NEW_RETENTION,
      check: NEW_CHECK,
      checkReadPercent: '5',
      uploadLimitKib: '0',
      downloadLimitKib: '0',
      overdueHours: '',
      excludes: '',
    };
  }
  const { settings } = job;
  return {
    name: settings.name,
    targetId: job.targetId,
    repository: job.repository,
    repositoryTouched: true,
    existing: false,
    existingKey: '',
    sources: settings.sources.map(({ shareId, path }) => ({ shareId, path })),
    schedule: scheduleOf(settings.schedule),
    enabled: settings.enabled,
    retention: Object.fromEntries(
      RETENTION_KEYS.map((key) => [key, String(settings.retention[key])]),
    ) as Record<RetentionKey, string>,
    check:
      settings.checkSchedule === undefined || settings.checkSchedule === null
        ? null
        : scheduleOf(settings.checkSchedule),
    checkReadPercent: String(settings.checkReadPercent),
    uploadLimitKib: String(settings.uploadLimitKib),
    downloadLimitKib: String(settings.downloadLimitKib),
    overdueHours:
      settings.overdueHours === undefined || settings.overdueHours === null
        ? ''
        : String(settings.overdueHours),
    excludes: (settings.excludes ?? []).join('\n'),
  };
}

/** A whole number from 0 up, or undefined. */
export function count(text: string): number | undefined {
  const trimmed = text.trim();
  return /^\d{1,9}$/.test(trimmed) ? Number(trimmed) : undefined;
}

/** A folder name from the job's name for its repository: lowercase letters, digits and dashes. */
export function repositoryFor(name: string): string {
  return name
    .normalize('NFKD')
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 48);
}

/** The settings the daemon takes, or undefined while a field cannot be one; the daemon checks the rest. */
function settingsOf(draft: Draft): BackupJobSettings | undefined {
  const schedule = cronOf(draft.schedule);
  const checkSchedule = draft.check === null ? null : cronOf(draft.check);
  const retention = Object.fromEntries(
    RETENTION_KEYS.map((key) => [key, count(draft.retention[key])]),
  ) as Record<RetentionKey, number | undefined>;
  const checkReadPercent = count(draft.checkReadPercent);
  const uploadLimitKib = count(draft.uploadLimitKib);
  const downloadLimitKib = count(draft.downloadLimitKib);
  const overdueHours = draft.overdueHours.trim() === '' ? null : count(draft.overdueHours);
  const sources = draft.sources.flatMap(({ shareId, path }) =>
    shareId === null ? [] : [{ shareId, path: path.trim() }],
  );
  if (
    draft.name.trim() === '' ||
    sources.length === 0 ||
    schedule === undefined ||
    checkSchedule === undefined ||
    RETENTION_KEYS.some((key) => retention[key] === undefined) ||
    checkReadPercent === undefined ||
    uploadLimitKib === undefined ||
    downloadLimitKib === undefined ||
    overdueHours === undefined
  ) {
    return undefined;
  }
  return {
    name: draft.name.trim(),
    sources,
    schedule,
    enabled: draft.enabled,
    retention: retention as unknown as BackupRetention,
    checkSchedule,
    checkReadPercent,
    uploadLimitKib,
    downloadLimitKib,
    overdueHours,
    excludes: draft.excludes
      .split('\n')
      .map((line) => line.trim())
      .filter((line) => line !== ''),
  };
}

export function closeOn(onClose: () => void) {
  return (open: boolean) => {
    if (!open) {
      onClose();
    }
  };
}

/** A schedule: a preset with its time and day, or a cron expression. */
export function ScheduleField({
  label,
  hint,
  value,
  onChange,
}: {
  label: string;
  hint: string;
  value: Schedule;
  onChange: (schedule: Schedule) => void;
}) {
  const { t } = useTranslation('backup');
  const weekdays = new Intl.DateTimeFormat(undefined, { weekday: 'long' });
  // 2026-11-22 is a Sunday: day 0 of cron.
  const weekday = (day: number) => weekdays.format(new Date(Date.UTC(2026, 10, 22 + day, 12)));
  return (
    <div className={styles.field}>
      <div className={styles.inline}>
        <Select
          label={label}
          options={SCHEDULE_KINDS.map((kind) => ({ id: kind, label: t(`jobForm.every.${kind}`) }))}
          value={value.kind}
          onChange={(kind) => {
            const next = SCHEDULE_KINDS.find((entry) => entry === kind);
            if (next !== undefined) {
              onChange(switchKind(value, next));
            }
          }}
        />
        {value.kind === 'hourly' && (
          <TextField
            label={t('jobForm.minute')}
            inputMode="numeric"
            value={String(value.minute)}
            onChange={(text) => {
              const minute = count(text);
              if (minute !== undefined && minute < 60) {
                onChange({ kind: 'hourly', minute });
              }
            }}
          />
        )}
        {value.kind === 'weekly' && (
          <Select
            label={t('jobForm.weekday')}
            options={WEEKDAYS.map((day) => ({ id: String(day), label: weekday(day) }))}
            value={String(value.day)}
            onChange={(day) => {
              onChange({ ...value, day: Number(day) });
            }}
          />
        )}
        {value.kind === 'monthly' && (
          <Select
            label={t('jobForm.dayOfMonth')}
            options={Array.from({ length: 28 }, (_, index) => ({
              id: String(index + 1),
              label: String(index + 1),
            }))}
            value={String(value.day)}
            onChange={(day) => {
              onChange({ ...value, day: Number(day) });
            }}
          />
        )}
        {'time' in value && (
          <TextField
            label={t('jobForm.time')}
            placeholder="02:00"
            inputMode="numeric"
            value={value.time}
            onChange={(time) => {
              onChange({ ...value, time });
            }}
          />
        )}
        {value.kind === 'custom' && (
          <TextField
            label={t('jobForm.cron')}
            spellCheck="false"
            value={value.cron}
            onChange={(cron) => {
              onChange({ kind: 'custom', cron });
            }}
          />
        )}
      </div>
      <p className={styles.hint}>
        {value.kind === 'custom' ? t('jobForm.cronHint') : hint}
        {cronOf(value) === undefined && ` ${t('jobForm.timeInvalid')}`}
      </p>
    </div>
  );
}

/** The folders a job backs up: a shared folder or home each, with a folder inside it. */
function SourcesField({
  app,
  sources,
  onChange,
}: {
  app: PithyxApp;
  sources: readonly SourceDraft[];
  onChange: (sources: SourceDraft[]) => void;
}) {
  const { t } = useTranslation('backup');
  const options = useQuery({
    queryKey: sourcesKey,
    queryFn: () => backupsCall(app, 'internal.admin.backups.sources', {}),
  });
  const list = options.data?.sources ?? [];
  const label = (option: (typeof list)[number]) =>
    !option.supported
      ? t('jobForm.sourceUnsupported', { name: option.name })
      : !option.available
        ? t('jobForm.sourceUnavailable', { name: option.name })
        : option.name;
  const replace = (index: number, source: SourceDraft) => {
    onChange(sources.map((current, at) => (at === index ? source : current)));
  };
  return (
    <fieldset className={styles.fieldset}>
      <legend className={styles.fieldLabel}>{t('jobForm.sources')}</legend>
      <p className={styles.hint}>{t('jobForm.sourcesHint')}</p>
      {options.isError && <ErrorNotice error={describeProblem(options.error)} />}
      {sources.map((source, index) => (
        <div key={index} className={styles.inline}>
          <Select
            label={t('jobForm.share')}
            placeholder={t('jobForm.sharePlaceholder')}
            options={list.map((option) => ({ id: String(option.shareId), label: label(option) }))}
            value={source.shareId === null ? null : String(source.shareId)}
            onChange={(id) => {
              replace(index, { ...source, shareId: id === null ? null : Number(id) });
            }}
          />
          <TextField
            label={t('jobForm.folder')}
            placeholder={t('jobForm.folderPlaceholder')}
            spellCheck="false"
            value={source.path}
            onChange={(path) => {
              replace(index, { ...source, path });
            }}
          />
          {sources.length > 1 && (
            <Button
              aria-label={t('jobForm.removeSource')}
              onPress={() => {
                onChange(sources.filter((_, at) => at !== index));
              }}
            >
              <IconTrash size={16} stroke={1.75} aria-hidden />
            </Button>
          )}
        </div>
      ))}
      <div className={styles.row}>
        <Button
          size="sm"
          isDisabled={sources.length >= 32}
          onPress={() => {
            onChange([...sources, { shareId: null, path: '' }]);
          }}
        >
          <IconPlus size={16} stroke={1.75} aria-hidden />
          {t('jobForm.addSource')}
        </Button>
      </div>
    </fieldset>
  );
}

/** Patterns to leave out, one per line; the hint names the patterns of the tool that reads them. */
export function Excludes({
  value,
  hint,
  onChange,
}: {
  value: string;
  hint?: string;
  onChange: (value: string) => void;
}) {
  const { t } = useTranslation('backup');
  const id = useId();
  return (
    <div className={styles.field}>
      <label htmlFor={id} className={styles.fieldLabel}>
        {t('jobForm.excludes')}
      </label>
      <textarea
        id={id}
        className={styles.textarea}
        value={value}
        rows={3}
        spellCheck={false}
        autoCapitalize="none"
        aria-describedby={`${id}-hint`}
        onChange={(event) => {
          onChange(event.target.value);
        }}
      />
      <span id={`${id}-hint`} className={styles.hint}>
        {hint ?? t('jobForm.excludesHint')}
      </span>
    </div>
  );
}

/** How many versions of each period a backup keeps, as typed. */
export function RetentionField({
  value,
  onChange,
}: {
  value: Record<RetentionKey, string>;
  onChange: (value: Record<RetentionKey, string>) => void;
}) {
  const { t } = useTranslation('backup');
  return (
    <fieldset className={styles.fieldset}>
      <legend className={styles.fieldLabel}>{t('jobForm.retention')}</legend>
      <p className={styles.hint}>{t('jobForm.retentionHint')}</p>
      <div className={styles.numbers}>
        {RETENTION_KEYS.map((key) => (
          <TextField
            key={key}
            label={t(`jobForm.keep.${key}`)}
            inputMode="numeric"
            value={value[key]}
            onChange={(entry) => {
              onChange({ ...value, [key]: entry });
            }}
          />
        ))}
      </div>
    </fieldset>
  );
}

/**
 * A new job, or the settings of one. A new job makes its repository with a key the box shows once, or connects to an
 * existing repository with its key; the target and the repository of a saved job stay.
 */
export function JobDialog({
  app,
  job,
  targets,
  onClose,
  onCreated,
}: {
  app: PithyxApp;
  job?: BackupJob;
  targets: readonly BackupTarget[];
  onClose: () => void;
  onCreated?: (job: BackupJob, key: string) => void;
}) {
  const { t } = useTranslation('backup');
  const queryClient = useQueryClient();
  const [draft, setDraft] = useState(() => draftOf(job, targets));
  const update = (change: Partial<Draft>) => {
    setDraft((current) => ({ ...current, ...change }));
  };
  const settings = settingsOf(draft);
  const save = useMutation({
    mutationFn: async (values: BackupJobSettings) => {
      if (job !== undefined) {
        await backupsCall(app, 'internal.admin.backups.updateJob', {
          id: job.id,
          settings: values,
        });
        return null;
      }
      return backupsCall(app, 'internal.admin.backups.createJob', {
        targetId: draft.targetId ?? 0,
        repository: draft.repository.trim(),
        existingKey: draft.existing ? draft.existingKey.trim() : null,
        settings: values,
      });
    },
    onSuccess: async (created) => {
      await queryClient.invalidateQueries({ queryKey: jobsKey });
      if (created?.key != null) {
        onCreated?.(created.job, created.key);
      } else {
        onClose();
      }
    },
  });
  const ready =
    settings !== undefined &&
    (job !== undefined ||
      (draft.targetId !== null &&
        draft.repository.trim() !== '' &&
        (!draft.existing || draft.existingKey.trim() !== '')));

  return (
    <Dialog
      isOpen
      onOpenChange={closeOn(onClose)}
      title={
        job === undefined
          ? t('jobForm.addTitle')
          : t('jobForm.editTitle', { name: job.settings.name })
      }
      size="medium"
      actions={
        <>
          <Button onPress={onClose}>{t('form.cancel')}</Button>
          <Button
            variant="primary"
            isDisabled={!ready || save.isPending}
            onPress={() => {
              if (settings !== undefined) {
                save.mutate(settings);
              }
            }}
          >
            {job === undefined ? t('jobForm.create') : t('form.save')}
          </Button>
        </>
      }
    >
      <div className={styles.form}>
        <TextField
          label={t('jobForm.name')}
          value={draft.name}
          onChange={(name) => {
            update(
              draft.repositoryTouched || job !== undefined
                ? { name }
                : { name, repository: repositoryFor(name) },
            );
          }}
          isRequired
        />
        {job === undefined ? (
          <>
            <Select
              label={t('jobForm.target')}
              placeholder={t('jobForm.targetPlaceholder')}
              options={targets.map((target) => ({ id: String(target.id), label: target.name }))}
              value={draft.targetId === null ? null : String(draft.targetId)}
              onChange={(id) => {
                update({ targetId: id === null ? null : Number(id) });
              }}
            />
            <TextField
              label={t('jobForm.repository')}
              description={t('jobForm.repositoryHint')}
              spellCheck="false"
              value={draft.repository}
              onChange={(repository) => {
                update({ repository, repositoryTouched: true });
              }}
              isRequired
            />
            <Checkbox
              isSelected={draft.existing}
              onChange={(existing) => {
                update({ existing });
              }}
            >
              {t('jobForm.existing')}
            </Checkbox>
            {draft.existing && (
              <TextField
                label={t('jobForm.existingKey')}
                description={t('jobForm.existingKeyHint')}
                type="password"
                autoComplete="off"
                spellCheck="false"
                value={draft.existingKey}
                onChange={(existingKey) => {
                  update({ existingKey });
                }}
              />
            )}
          </>
        ) : (
          <p className={styles.hint}>
            {t('jobForm.repositoryFixed', {
              repository: job.repository,
              target: targets.find((target) => target.id === job.targetId)?.name ?? '',
            })}
          </p>
        )}
        <SourcesField
          app={app}
          sources={draft.sources}
          onChange={(sources) => {
            update({ sources });
          }}
        />
        <ScheduleField
          label={t('jobForm.schedule')}
          hint={t('jobForm.scheduleHint')}
          value={draft.schedule}
          onChange={(schedule) => {
            update({ schedule });
          }}
        />
        <Switch
          isSelected={draft.enabled}
          onChange={(enabled) => {
            update({ enabled });
          }}
        >
          {t('jobForm.enabled')}
        </Switch>
        <RetentionField
          value={draft.retention}
          onChange={(retention) => {
            update({ retention });
          }}
        />
        <fieldset className={styles.fieldset}>
          <legend className={styles.fieldLabel}>{t('jobForm.check')}</legend>
          <Checkbox
            isSelected={draft.check !== null}
            onChange={(on) => {
              update({ check: on ? NEW_CHECK : null });
            }}
          >
            {t('jobForm.checkOn')}
          </Checkbox>
          {draft.check !== null && (
            <>
              <ScheduleField
                label={t('jobForm.checkSchedule')}
                hint={t('jobForm.checkHint')}
                value={draft.check}
                onChange={(check) => {
                  update({ check });
                }}
              />
              <TextField
                label={t('jobForm.checkReadPercent')}
                description={t('jobForm.checkReadPercentHint')}
                inputMode="numeric"
                value={draft.checkReadPercent}
                onChange={(checkReadPercent) => {
                  update({ checkReadPercent });
                }}
              />
            </>
          )}
        </fieldset>
        <fieldset className={styles.fieldset}>
          <legend className={styles.fieldLabel}>{t('jobForm.advanced')}</legend>
          <div className={styles.numbers}>
            <TextField
              label={t('jobForm.upload')}
              inputMode="numeric"
              value={draft.uploadLimitKib}
              onChange={(uploadLimitKib) => {
                update({ uploadLimitKib });
              }}
            />
            <TextField
              label={t('jobForm.download')}
              inputMode="numeric"
              value={draft.downloadLimitKib}
              onChange={(downloadLimitKib) => {
                update({ downloadLimitKib });
              }}
            />
          </div>
          <p className={styles.hint}>{t('jobForm.limitHint')}</p>
          <TextField
            label={t('jobForm.overdue')}
            description={t('jobForm.overdueHint')}
            inputMode="numeric"
            value={draft.overdueHours}
            onChange={(overdueHours) => {
              update({ overdueHours });
            }}
          />
          <Excludes
            value={draft.excludes}
            onChange={(excludes) => {
              update({ excludes });
            }}
          />
        </fieldset>
        {save.isError && <ErrorNotice error={describeProblem(save.error)} />}
      </div>
    </Dialog>
  );
}

function download(text: string, fileName: string) {
  const url = URL.createObjectURL(new Blob([text], { type: 'text/plain;charset=utf-8' }));
  const link = document.createElement('a');
  link.href = url;
  link.download = fileName;
  link.rel = 'noopener';
  document.body.append(link);
  link.click();
  link.remove();
  setTimeout(() => {
    URL.revokeObjectURL(url);
  }, 60_000);
}

/**
 * The key of a job's repository: copy or download it. A new job's key is shown once, so the administrator confirms it
 * is stored before closing; a key shown again after the password closes freely.
 */
export function KeyDialog({
  job,
  repositoryKey,
  targetName,
  onClose,
  mustConfirm = true,
}: {
  job: BackupJob;
  repositoryKey: string;
  targetName: string;
  onClose: () => void;
  mustConfirm?: boolean;
}) {
  const { t } = useTranslation('backup');
  const [copied, setCopied] = useState<'copied' | 'copyManually' | null>(null);
  const [stored, setStored] = useState(!mustConfirm);
  const value = useRef<HTMLElement>(null);
  const name = job.settings.name;
  return (
    <Dialog
      isOpen
      role="alertdialog"
      onOpenChange={(open) => {
        if (!open && stored) {
          onClose();
        }
      }}
      title={t('key.title', { name })}
      actions={
        <Button variant="primary" isDisabled={!stored} onPress={onClose}>
          {t('form.close')}
        </Button>
      }
    >
      <div className={styles.form}>
        <p className={styles.warning}>{t(mustConfirm ? 'key.warning' : 'key.warningAgain')}</p>
        <code ref={value} className={styles.key}>
          {repositoryKey}
        </code>
        <div className={styles.row}>
          <Button
            size="sm"
            onPress={() => {
              void copyText(repositoryKey, value.current).then((done) => {
                setCopied(done ? 'copied' : 'copyManually');
              });
            }}
          >
            <IconCopy size={16} stroke={1.75} aria-hidden />
            {t('key.copy')}
          </Button>
          <Button
            size="sm"
            onPress={() => {
              download(
                `${t('key.fileIntro', { name, target: targetName, repository: job.repository })}\n\n${repositoryKey}\n`,
                `pithyx-backup-key-${repositoryFor(name) || String(job.id)}.txt`,
              );
            }}
          >
            <IconDownload size={16} stroke={1.75} aria-hidden />
            {t('key.download')}
          </Button>
          {copied !== null && (
            <span className={styles.muted} role="status">
              {t(`key.${copied}`)}
            </span>
          )}
        </div>
        {mustConfirm && (
          <Checkbox isSelected={stored} onChange={setStored}>
            {t('key.stored')}
          </Checkbox>
        )}
      </div>
    </Dialog>
  );
}

/** Shows a job's key again after the password and, with two-factor sign-in on, a current code (#404). */
export function RevealKeyDialog({
  app,
  job,
  targetName,
  onClose,
}: {
  app: PithyxApp;
  job: BackupJob;
  targetName: string;
  onClose: () => void;
}) {
  const { t } = useTranslation('backup');
  const twoFactor = app.context.user.twoFactor;
  const [password, setPassword] = useState('');
  const [code, setCode] = useState('');
  const reveal = useMutation({
    mutationFn: () =>
      backupsCall(app, 'internal.admin.backups.revealKey', {
        id: job.id,
        password,
        ...(twoFactor ? { totpCode: code.trim() } : {}),
      }),
    onSettled: () => {
      setPassword('');
      setCode('');
    },
  });
  if (reveal.data !== undefined) {
    return (
      <KeyDialog
        job={job}
        repositoryKey={reveal.data.key}
        targetName={targetName}
        onClose={onClose}
        mustConfirm={false}
      />
    );
  }
  const ready = password !== '' && (!twoFactor || code.trim() !== '');
  return (
    <Dialog
      isOpen
      onOpenChange={closeOn(onClose)}
      title={t('key.title', { name: job.settings.name })}
      actions={
        <>
          <Button onPress={onClose}>{t('form.cancel')}</Button>
          <Button
            variant="primary"
            type="submit"
            form="backup-reveal-key"
            isDisabled={!ready || reveal.isPending}
          >
            {t('key.reveal')}
          </Button>
        </>
      }
    >
      <form
        id="backup-reveal-key"
        className={styles.form}
        onSubmit={(event) => {
          event.preventDefault();
          if (ready) {
            reveal.mutate();
          }
        }}
      >
        <p className={styles.muted}>{t('key.stepUp', { twoFactor: String(twoFactor) })}</p>
        <PasswordField
          label={t('key.password')}
          value={password}
          onChange={setPassword}
          autoComplete="current-password"
          autoFocus
        />
        {twoFactor && (
          <TextField
            label={t('key.code')}
            value={code}
            onChange={setCode}
            inputMode="numeric"
            autoComplete="one-time-code"
          />
        )}
        {reveal.isError && <ErrorNotice error={describeProblem(reveal.error)} />}
      </form>
    </Dialog>
  );
}

/**
 * Deletes a job after a confirmation; the repository stays on the target. A server backup's folder stays too, unless
 * the administrator chooses to delete it with its versions.
 */
export function DeleteJobDialog({
  app,
  job,
  destination,
  onClose,
}: {
  app: PithyxApp;
  job: BackupJob;
  /** Where a server backup's folder is, as the app names it. */
  destination: string;
  onClose: () => void;
}) {
  const { t } = useTranslation('backup');
  const queryClient = useQueryClient();
  const [data, setData] = useState(false);
  const remove = useMutation({
    mutationFn: () =>
      backupsCall(
        app,
        'internal.admin.backups.deleteJob',
        data ? { id: job.id, data } : { id: job.id },
      ),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: jobsKey });
      onClose();
    },
  });
  return (
    <Dialog
      isOpen
      role="alertdialog"
      onOpenChange={closeOn(onClose)}
      title={t('deleteJob.title', { name: job.settings.name })}
      actions={
        <>
          <Button onPress={onClose}>{t('form.cancel')}</Button>
          <Button
            variant="danger"
            isDisabled={remove.isPending}
            onPress={() => {
              remove.mutate();
            }}
          >
            {t('deleteJob.confirm')}
          </Button>
        </>
      }
    >
      <div className={styles.form}>
        {job.kind === 'pull' ? (
          <>
            <p className={styles.muted}>{t('deleteJobPull.text', { destination })}</p>
            <Checkbox isSelected={data} onChange={setData}>
              {t('deleteJobPull.data')}
            </Checkbox>
            {data && (
              <p className={styles.warning}>
                {t('deleteJobPull.dataHint', { dataset: job.dataset ?? '' })}
              </p>
            )}
          </>
        ) : (
          <p className={styles.muted}>{t('deleteJob.text')}</p>
        )}
        {remove.isError && <ErrorNotice error={describeProblem(remove.error)} />}
      </div>
    </Dialog>
  );
}
