import type { PithyxApp } from '@pithyx/sdk';
import { Button, Dialog, Radio, RadioGroup, Select, Switch, TextField } from '@pithyx/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import {
  backupsCall,
  jobsKey,
  sourcesKey,
  type BackupJob,
  type BackupJobSettings,
  type BackupPullSettings,
  type BackupRetention,
  type BackupTarget,
} from '../sdk/backups';
import { describeProblem } from '../storage/shared';
import {
  Excludes,
  NEW_RETENTION,
  NEW_SCHEDULE,
  RETENTION_KEYS,
  RetentionField,
  ScheduleField,
  closeOn,
  count,
  repositoryFor,
  type RetentionKey,
} from './JobDialogs';
import { cronOf, scheduleOf, type Schedule } from './schedule';
import styles from './Backup.module.css';

/** What the administrator edits; numbers and paths as typed. */
interface Draft {
  name: string;
  serverId: number | null;
  shareId: number | null;
  folder: string;
  folderTouched: boolean;
  paths: string;
  delete: boolean;
  owners: BackupPullSettings['owners'];
  schedule: Schedule;
  enabled: boolean;
  retention: Record<RetentionKey, string>;
  downloadLimitKib: string;
  overdueHours: string;
  excludes: string;
}

function draftOf(job: BackupJob | undefined, servers: readonly BackupTarget[]): Draft {
  if (job?.pull == null) {
    return {
      name: '',
      serverId: servers.length === 1 ? (servers[0]?.id ?? null) : null,
      shareId: null,
      folder: '',
      folderTouched: false,
      paths: '',
      delete: true,
      owners: 'server',
      schedule: NEW_SCHEDULE,
      enabled: true,
      retention: NEW_RETENTION,
      downloadLimitKib: '0',
      overdueHours: '',
      excludes: '',
    };
  }
  const { settings, pull } = job;
  return {
    name: settings.name,
    serverId: job.targetId,
    shareId: pull.shareId,
    folder: pull.folder,
    folderTouched: true,
    paths: pull.paths.join('\n'),
    delete: pull.delete,
    owners: pull.owners,
    schedule: scheduleOf(settings.schedule),
    enabled: settings.enabled,
    retention: Object.fromEntries(
      RETENTION_KEYS.map((key) => [key, String(settings.retention[key])]),
    ) as Record<RetentionKey, string>,
    downloadLimitKib: String(settings.downloadLimitKib),
    overdueHours:
      settings.overdueHours === undefined || settings.overdueHours === null
        ? ''
        : String(settings.overdueHours),
    excludes: (settings.excludes ?? []).join('\n'),
  };
}

const lines = (text: string) =>
  text
    .split('\n')
    .map((line) => line.trim())
    .filter((line) => line !== '');

/**
 * The settings and what to pull as the daemon takes them, or undefined while a field cannot be one; the daemon checks
 * the rest. A server backup has no sources, no check and no upload.
 */
function requestOf(
  draft: Draft,
): { settings: BackupJobSettings; pull: BackupPullSettings } | undefined {
  const schedule = cronOf(draft.schedule);
  const retention = Object.fromEntries(
    RETENTION_KEYS.map((key) => [key, count(draft.retention[key])]),
  ) as Record<RetentionKey, number | undefined>;
  const downloadLimitKib = count(draft.downloadLimitKib);
  const overdueHours = draft.overdueHours.trim() === '' ? null : count(draft.overdueHours);
  const paths = lines(draft.paths);
  if (
    draft.name.trim() === '' ||
    draft.shareId === null ||
    draft.folder.trim() === '' ||
    paths.length === 0 ||
    schedule === undefined ||
    RETENTION_KEYS.some((key) => retention[key] === undefined) ||
    downloadLimitKib === undefined ||
    overdueHours === undefined
  ) {
    return undefined;
  }
  return {
    settings: {
      name: draft.name.trim(),
      sources: [],
      schedule,
      enabled: draft.enabled,
      retention: retention as unknown as BackupRetention,
      checkSchedule: null,
      checkReadPercent: 0,
      uploadLimitKib: 0,
      downloadLimitKib,
      overdueHours,
      excludes: lines(draft.excludes),
    },
    pull: {
      shareId: draft.shareId,
      folder: draft.folder.trim(),
      paths,
      delete: draft.delete,
      owners: draft.owners,
    },
  };
}

/**
 * A new server backup (#396, D420), or the settings of one: which server, which of its folders, the folder of a shared
 * folder it lands in (a dataset of its own), and when it runs and what it keeps. The server and the folder on the box
 * of a saved backup stay.
 */
export function PullDialog({
  app,
  job,
  servers,
  destination,
  onClose,
}: {
  app: PithyxApp;
  job?: BackupJob;
  servers: readonly BackupTarget[];
  /** Where a saved backup's folder is, as the app names it. */
  destination?: string;
  onClose: () => void;
}) {
  const { t } = useTranslation('backup');
  const queryClient = useQueryClient();
  const pathsId = useId();
  const [draft, setDraft] = useState(() => draftOf(job, servers));
  const update = (change: Partial<Draft>) => {
    setDraft((current) => ({ ...current, ...change }));
  };
  const shares = useQuery({
    queryKey: sourcesKey,
    queryFn: () => backupsCall(app, 'internal.admin.backups.sources', {}),
    enabled: job === undefined,
  });
  // A server backup lands in a shared folder; homes belong to their users.
  const shareOptions = (shares.data?.sources ?? []).filter((option) => !option.home);
  const request = requestOf(draft);
  const save = useMutation({
    mutationFn: async (values: NonNullable<ReturnType<typeof requestOf>>) => {
      if (job !== undefined) {
        await backupsCall(app, 'internal.admin.backups.updatePull', { id: job.id, ...values });
        return;
      }
      await backupsCall(app, 'internal.admin.backups.createPull', {
        serverId: draft.serverId ?? 0,
        ...values,
      });
    },
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: jobsKey });
      onClose();
    },
  });
  const ready = request !== undefined && (job !== undefined || draft.serverId !== null);

  return (
    <Dialog
      isOpen
      onOpenChange={closeOn(onClose)}
      title={
        job === undefined
          ? t('pullForm.addTitle')
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
              if (request !== undefined) {
                save.mutate(request);
              }
            }}
          >
            {job === undefined ? t('pullForm.create') : t('form.save')}
          </Button>
        </>
      }
    >
      <div className={styles.form}>
        <TextField
          label={t('jobForm.name')}
          value={draft.name}
          onChange={(name) => {
            update(draft.folderTouched ? { name } : { name, folder: repositoryFor(name) });
          }}
          isRequired
        />
        {job === undefined ? (
          <>
            <Select
              label={t('pullForm.server')}
              placeholder={t('pullForm.serverPlaceholder')}
              options={servers.map((server) => ({ id: String(server.id), label: server.name }))}
              value={draft.serverId === null ? null : String(draft.serverId)}
              onChange={(id) => {
                update({ serverId: id === null ? null : Number(id) });
              }}
            />
            {shares.isError && <ErrorNotice error={describeProblem(shares.error)} />}
            <Select
              label={t('pullForm.share')}
              placeholder={t('pullForm.sharePlaceholder')}
              options={shareOptions.map((option) => ({
                id: String(option.shareId),
                label: !option.supported
                  ? t('jobForm.sourceUnsupported', { name: option.name })
                  : !option.available
                    ? t('jobForm.sourceUnavailable', { name: option.name })
                    : option.name,
              }))}
              value={draft.shareId === null ? null : String(draft.shareId)}
              onChange={(id) => {
                update({ shareId: id === null ? null : Number(id) });
              }}
            />
            <TextField
              label={t('pullForm.folder')}
              description={t('pullForm.folderHint')}
              spellCheck="false"
              autoCapitalize="none"
              value={draft.folder}
              onChange={(folder) => {
                update({ folder, folderTouched: true });
              }}
              isRequired
            />
            <p className={styles.warning}>{t('pullForm.readable')}</p>
          </>
        ) : (
          <p className={styles.hint}>
            {t('pullForm.fixed', {
              server: servers.find((server) => server.id === job.targetId)?.name ?? '',
              destination: destination ?? '',
            })}
          </p>
        )}
        <div className={styles.field}>
          <label htmlFor={pathsId} className={styles.fieldLabel}>
            {t('pullForm.paths')}
          </label>
          <textarea
            id={pathsId}
            className={styles.textarea}
            value={draft.paths}
            rows={3}
            spellCheck={false}
            autoCapitalize="none"
            required
            placeholder={'/etc\n/srv/www'}
            aria-describedby={`${pathsId}-hint`}
            onChange={(event) => {
              update({ paths: event.target.value });
            }}
          />
          <span id={`${pathsId}-hint`} className={styles.hint}>
            {t('pullForm.pathsHint')}
          </span>
        </div>
        <Switch
          isSelected={draft.delete}
          onChange={(remove) => {
            update({ delete: remove });
          }}
        >
          {t('pullForm.delete')}
        </Switch>
        <p className={styles.hint}>{t('pullForm.deleteHint')}</p>
        <RadioGroup
          label={t('pullForm.owners')}
          value={draft.owners}
          onChange={(owners) => {
            update({ owners: owners === 'box' ? 'box' : 'server' });
          }}
        >
          <Radio value="server">{t('pullForm.ownersServer')}</Radio>
          <Radio value="box">{t('pullForm.ownersBox')}</Radio>
        </RadioGroup>
        <ScheduleField
          label={t('pullForm.schedule')}
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
          <legend className={styles.fieldLabel}>{t('jobForm.advanced')}</legend>
          <TextField
            label={t('jobForm.download')}
            description={t('jobForm.limitHint')}
            inputMode="numeric"
            value={draft.downloadLimitKib}
            onChange={(downloadLimitKib) => {
              update({ downloadLimitKib });
            }}
          />
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
            hint={t('pullForm.excludesHint')}
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
