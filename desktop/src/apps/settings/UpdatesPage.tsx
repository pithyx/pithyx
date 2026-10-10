import type { PithyxApp } from '@pithyx/sdk';
import { Badge, Button, Checkbox, Dialog, Switch, TextField } from '@pithyx/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { useFormatLocale } from '../../i18n';
import {
  onUpdates,
  updatesCall,
  updatesKey,
  UPDATES_JOB,
  UPDATES_STATE,
  type Job,
  type SnapshotInfo,
  type UpdateStatus,
} from '../sdk/updates';
import { describeProblem } from '../storage/shared';
import styles from './Settings.module.css';

const STEPS = ['download', 'snapshot', 'install', 'debian', 'bootloader', 'restart'] as const;

/**
 * Settings > Updates (D297, D338): the installed version and the channel, what the daily check found (it only
 * notifies), installing the channel's newer version or an uploaded file with a snapshot before and a health check
 * after the restart, the history and the rollback to a snapshot, and the update source (D339), which only the recovery
 * menu switches. Administrators only.
 */
export function UpdatesPage({ app }: { app: PithyxApp }) {
  const { t } = useTranslation('settings');
  const queryClient = useQueryClient();
  const status = useQuery({
    queryKey: updatesKey,
    queryFn: () => updatesCall(app, 'internal.admin.updates.get', {}),
  });
  const [jobs, setJobs] = useState<Readonly<Record<number, Job>>>({});
  // The desktop forwards system/updates and the started jobs to this window: syncing with the SDK's events.
  useEffect(() => {
    const stops = [
      onUpdates(app, UPDATES_STATE, (data) => {
        queryClient.setQueryData<UpdateStatus>(updatesKey, data as UpdateStatus);
      }),
      onUpdates(app, UPDATES_JOB, (data) => {
        const job = data as Job;
        setJobs((current) => ({ ...current, [job.id]: job }));
      }),
    ];
    return () => {
      for (const stop of stops) {
        stop();
      }
    };
  }, [app, queryClient]);
  const [started, setStarted] = useState<Job | null>(null);
  const job = started === null ? null : (jobs[started.id] ?? started);
  const [installing, setInstalling] = useState<'online' | 'file' | null>(null);
  const [rollback, setRollback] = useState<SnapshotInfo | null>(null);

  const data = status.data;
  const busy = data?.runningJobId != null || (job !== null && job.state === 'running');

  return (
    <div className={styles.content}>
      <header className={styles.pageHeader}>
        <div>
          <h2 className={styles.pageTitle}>{t('pages.updates.title')}</h2>
          <p className={styles.muted}>{t('updates.intro')}</p>
        </div>
      </header>
      {status.isError && <ErrorNotice error={describeProblem(status.error)} />}
      {data !== undefined && (
        <>
          {!data.supported && (
            <div className={styles.notice} role="note">
              <p>{t('updates.unsupported', { reason: data.unsupportedReason ?? '' })}</p>
            </div>
          )}
          <Overview
            app={app}
            status={data}
            busy={busy}
            onInstall={() => {
              setInstalling('online');
            }}
          />
          {job !== null && <Progress job={job} />}
          <Upload
            app={app}
            status={data}
            busy={busy}
            onInstall={() => {
              setInstalling('file');
            }}
          />
          <History status={data} busy={busy} onRollback={setRollback} />
          <UpdateSettings app={app} status={data} />
          <Source app={app} status={data} />
          {installing !== null && (
            <InstallDialog
              app={app}
              status={data}
              source={installing}
              onStarted={setStarted}
              onClose={() => {
                setInstalling(null);
              }}
            />
          )}
          {rollback !== null && (
            <RollbackDialog
              app={app}
              snapshot={rollback}
              onStarted={setStarted}
              onClose={() => {
                setRollback(null);
              }}
            />
          )}
        </>
      )}
    </div>
  );
}

function Overview({
  app,
  status,
  busy,
  onInstall,
}: {
  app: PithyxApp;
  status: UpdateStatus;
  busy: boolean;
  onInstall: () => void;
}) {
  const { t } = useTranslation('settings');
  const locale = useFormatLocale();
  const queryClient = useQueryClient();
  const check = useMutation({
    mutationFn: () => updatesCall(app, 'internal.admin.updates.check', {}),
    onSuccess: (next) => queryClient.setQueryData(updatesKey, next),
  });
  const date = (value: string | null | undefined) =>
    value == null
      ? t('updates.never')
      : new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeStyle: 'short' }).format(
          new Date(value),
        );
  const available = status.available ?? null;
  const facts: [string, string][] = [
    [t('updates.installed'), status.installedVersion],
    [t('updates.channel'), t(`updates.channels.${status.channel === 'beta' ? 'beta' : 'stable'}`)],
    [t('updates.lastCheck'), date(status.lastCheckAt)],
    [
      t('updates.nextCheck'),
      status.checkEnabled ? date(status.nextCheckAt) : t('updates.checkOff'),
    ],
  ];
  return (
    <section className={styles.card} aria-labelledby="updates-pithyx">
      <h3 id="updates-pithyx" className={styles.sectionTitle}>
        {t('updates.pithyx')}
      </h3>
      <dl className={styles.facts}>
        {facts.map(([label, value]) => (
          <div key={label} className={styles.fact}>
            <dt>{label}</dt>
            <dd>{value}</dd>
          </div>
        ))}
      </dl>
      {status.problem != null && (
        <ErrorNotice
          error={describeProblem({ code: status.problem.code, params: status.problem.params })}
        />
      )}
      {available === null ? (
        <p role="status">{status.checking ? t('updates.checking') : t('updates.upToDate')}</p>
      ) : (
        <div className={styles.notice}>
          <p>
            <Badge>{t('updates.newVersion', { version: available.version })}</Badge>
          </p>
          {!available.installable && (
            <p>{t('updates.needsMinimum', { minimum: available.minimumVersion })}</p>
          )}
          <Changelog text={available.changelog} />
        </div>
      )}
      {status.debian != null && (
        <p className={styles.muted}>
          {t('updates.debian', { count: status.debian.count, security: status.debian.security })}
        </p>
      )}
      <div className={styles.buttons}>
        <Button
          variant="secondary"
          isDisabled={check.isPending || status.checking}
          onPress={() => {
            check.mutate();
          }}
        >
          {t('updates.checkNow')}
        </Button>
        {available !== null && (
          <Button
            variant="primary"
            isDisabled={busy || !status.supported || !available.installable}
            onPress={onInstall}
          >
            {t('updates.install', { version: available.version })}
          </Button>
        )}
      </div>
      {check.isError && <ErrorNotice error={describeProblem(check.error)} />}
    </section>
  );
}

function Changelog({ text }: { text: string }) {
  const { t } = useTranslation('settings');
  if (text.trim() === '') {
    return null;
  }
  return (
    <details>
      <summary>{t('updates.changelog')}</summary>
      <pre className={styles.changelog}>{text}</pre>
    </details>
  );
}

function Progress({ job }: { job: Job }) {
  const { t } = useTranslation('settings');
  const current = job.progress.currentItem ?? '';
  const reached = STEPS.indexOf(current as (typeof STEPS)[number]);
  return (
    <section className={styles.card} aria-labelledby="updates-progress" aria-live="polite">
      <h3 id="updates-progress" className={styles.sectionTitle}>
        {t(
          job.kind === 'system.rollback'
            ? 'updates.progress.rollbackTitle'
            : 'updates.progress.title',
        )}
      </h3>
      {job.kind !== 'system.rollback' && (
        <ol className={styles.steps}>
          {STEPS.map((step, index) => (
            <li
              key={step}
              aria-current={step === current ? 'step' : undefined}
              data-done={index < reached || job.state === 'succeeded' ? '' : undefined}
            >
              {t(`updates.steps.${step}`)}
            </li>
          ))}
        </ol>
      )}
      {job.state === 'failed' && job.errorCode !== null ? (
        <ErrorNotice
          error={describeProblem({
            code: job.errorCode,
            params: job.errorParams ?? {},
            requestId: job.requestId,
          })}
        />
      ) : (
        <p role="status">
          {job.state === 'succeeded'
            ? t(
                (job.result as { state?: string } | null)?.state === 'restarting'
                  ? 'updates.progress.restarting'
                  : 'updates.progress.done',
              )
            : t('updates.progress.running')}
        </p>
      )}
    </section>
  );
}

function Upload({
  app,
  status,
  busy,
  onInstall,
}: {
  app: PithyxApp;
  status: UpdateStatus;
  busy: boolean;
  onInstall: () => void;
}) {
  const { t } = useTranslation('settings');
  const queryClient = useQueryClient();
  const input = useRef<HTMLInputElement>(null);
  const upload = useMutation({
    mutationFn: (file: File) =>
      updatesCall(app, 'internal.admin.updates.upload', { file, name: file.name }),
    onSuccess: (next) => queryClient.setQueryData(updatesKey, next),
  });
  const discard = useMutation({
    mutationFn: () => updatesCall(app, 'internal.admin.updates.discard', {}),
    onSuccess: (next) => queryClient.setQueryData(updatesKey, next),
  });
  const staged = status.staged ?? null;
  return (
    <section className={styles.card} aria-labelledby="updates-upload">
      <h3 id="updates-upload" className={styles.sectionTitle}>
        {t('updates.upload.title')}
      </h3>
      <p className={styles.muted}>{t('updates.upload.intro')}</p>
      <input
        ref={input}
        type="file"
        accept=".pithyxupdate"
        hidden
        aria-label={t('updates.upload.choose')}
        onChange={(event) => {
          const file = event.target.files?.[0];
          if (file !== undefined) {
            upload.mutate(file);
          }
          event.target.value = '';
        }}
      />
      {staged !== null && (
        <div className={styles.notice} role="status">
          <p>
            <strong>{t('updates.upload.checked', { version: staged.version })}</strong>
          </p>
          <p>
            {staged.trustedSignature ? t('updates.upload.signed') : t('updates.upload.unsigned')}{' '}
            {t('updates.upload.details', {
              channel: staged.channel,
              packages: staged.packages,
              kernel: staged.kernel,
            })}
          </p>
          <Changelog text={staged.changelog} />
        </div>
      )}
      <div className={styles.buttons}>
        <Button
          variant={staged === null ? 'primary' : 'secondary'}
          isDisabled={upload.isPending || busy}
          onPress={() => input.current?.click()}
        >
          {upload.isPending ? t('updates.upload.checking') : t('updates.upload.choose')}
        </Button>
        {staged !== null && (
          <>
            <Button variant="primary" isDisabled={busy || !status.supported} onPress={onInstall}>
              {t('updates.install', { version: staged.version })}
            </Button>
            <Button
              variant="secondary"
              isDisabled={discard.isPending || busy}
              onPress={() => {
                discard.mutate();
              }}
            >
              {t('updates.upload.discard')}
            </Button>
          </>
        )}
      </div>
      {upload.isError && <ErrorNotice error={describeProblem(upload.error)} />}
    </section>
  );
}

function History({
  status,
  busy,
  onRollback,
}: {
  status: UpdateStatus;
  busy: boolean;
  onRollback: (snapshot: SnapshotInfo) => void;
}) {
  const { t } = useTranslation('settings');
  const locale = useFormatLocale();
  const date = (value: string) =>
    new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeStyle: 'short' }).format(
      new Date(value),
    );
  return (
    <>
      <section className={styles.card} aria-labelledby="updates-snapshots">
        <h3 id="updates-snapshots" className={styles.sectionTitle}>
          {t('updates.snapshots.title')}
        </h3>
        <p className={styles.muted}>{t('updates.snapshots.intro')}</p>
        {status.snapshots.length === 0 ? (
          <p className={styles.muted}>{t('updates.snapshots.none')}</p>
        ) : (
          <ul className={styles.snapshotList}>
            {status.snapshots.map((snapshot) => (
              <li key={snapshot.name}>
                <span>
                  {t('updates.snapshots.entry', {
                    version: snapshot.version,
                    date: date(snapshot.createdAt),
                  })}
                </span>
                <Button
                  variant="secondary"
                  isDisabled={busy || !status.supported}
                  onPress={() => {
                    onRollback(snapshot);
                  }}
                >
                  {t('updates.snapshots.rollback')}
                </Button>
              </li>
            ))}
          </ul>
        )}
      </section>
      <section className={styles.card} aria-labelledby="updates-history">
        <h3 id="updates-history" className={styles.sectionTitle}>
          {t('updates.history.title')}
        </h3>
        {status.history.length === 0 ? (
          <p className={styles.muted}>{t('updates.history.none')}</p>
        ) : (
          <table className={styles.table}>
            <thead>
              <tr>
                <th scope="col">{t('updates.history.date')}</th>
                <th scope="col">{t('updates.history.change')}</th>
                <th scope="col">{t('updates.history.result')}</th>
              </tr>
            </thead>
            <tbody>
              {status.history.map((entry) => (
                <tr key={entry.id}>
                  <td>{date(entry.startedAt)}</td>
                  <td>
                    {t(
                      entry.kind === 'rollback'
                        ? 'updates.history.rollback'
                        : 'updates.history.update',
                      {
                        from: entry.fromVersion,
                        to: entry.toVersion,
                        source: entry.source,
                      },
                    )}
                  </td>
                  <td>
                    {t(`updates.history.states.${entry.state}`, { defaultValue: entry.state })}
                    {entry.errorCode != null && (
                      <ErrorNotice
                        error={describeProblem({
                          code: entry.errorCode,
                          params: entry.errorParams ?? {},
                        })}
                      />
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>
    </>
  );
}

function UpdateSettings({ app, status }: { app: PithyxApp; status: UpdateStatus }) {
  const { t } = useTranslation('settings');
  const queryClient = useQueryClient();
  const save = useMutation({
    mutationFn: (next: { checkEnabled: boolean; channel: string }) =>
      updatesCall(app, 'internal.admin.updates.settings', next),
    onSuccess: (next) => queryClient.setQueryData(updatesKey, next),
  });
  return (
    <section className={styles.card} aria-labelledby="updates-settings">
      <h3 id="updates-settings" className={styles.sectionTitle}>
        {t('updates.settings.title')}
      </h3>
      <Switch
        isSelected={status.checkEnabled}
        isDisabled={save.isPending}
        onChange={(checkEnabled) => {
          save.mutate({ checkEnabled, channel: status.channel });
        }}
      >
        {t('updates.settings.check')}
      </Switch>
      <p className={styles.muted}>{t('updates.settings.checkHint')}</p>
      <label className={styles.selectField}>
        <span className={styles.fieldLabel}>{t('updates.channel')}</span>
        <select
          className={styles.select}
          value={status.channel}
          disabled={save.isPending}
          onChange={(event) => {
            save.mutate({ checkEnabled: status.checkEnabled, channel: event.target.value });
          }}
        >
          <option value="stable">{t('updates.channels.stable')}</option>
          <option value="beta">{t('updates.channels.beta')}</option>
        </select>
      </label>
      <p className={styles.muted}>{t('updates.settings.channelHint')}</p>
      {save.isError && <ErrorNotice error={describeProblem(save.error)} />}
    </section>
  );
}

type SourceKey = UpdateStatus['source']['keys'][number];

function SourceKeys({ keys }: { keys: readonly SourceKey[] }) {
  const { t } = useTranslation('settings');
  return (
    <ul className={styles.sourceKeys}>
      {keys.map((key) => (
        <li key={key.keyId}>
          {t('updates.source.key', { id: key.keyId, channels: key.channels.join(', ') })}{' '}
          <code className={styles.mono}>{key.publicKey}</code>
        </li>
      ))}
    </ul>
  );
}

/**
 * The update source the box follows and a source file saved for the recovery menu (D339). Switching happens only at the
 * console, so a taken-over account cannot redirect the box's updates; the file saved here spares typing a key there.
 */
function Source({ app, status }: { app: PithyxApp; status: UpdateStatus }) {
  const { t } = useTranslation('settings');
  const locale = useFormatLocale();
  const queryClient = useQueryClient();
  const input = useRef<HTMLInputElement>(null);
  const save = useMutation({
    mutationFn: async (file: File) =>
      updatesCall(app, 'internal.admin.updates.source.save', { text: await file.text() }),
    onSuccess: (next) => queryClient.setQueryData(updatesKey, next),
  });
  const discard = useMutation({
    mutationFn: () => updatesCall(app, 'internal.admin.updates.source.discard', {}),
    onSuccess: (next) => queryClient.setQueryData(updatesKey, next),
  });
  const { source } = status;
  const candidate = status.sourceCandidate ?? null;
  return (
    <section className={styles.card} aria-labelledby="updates-source">
      <h3 id="updates-source" className={styles.sectionTitle}>
        {t('updates.source.title')}
      </h3>
      <dl className={styles.facts}>
        <div className={styles.fact}>
          <dt>{t('updates.source.name')}</dt>
          <dd>
            {source.official ? t('updates.source.official', { name: source.name }) : source.name}
          </dd>
        </div>
        <div className={styles.fact}>
          <dt>{t('updates.source.url')}</dt>
          <dd className={styles.mono}>{source.url}</dd>
        </div>
      </dl>
      {!source.official && (
        <div className={styles.notice} role="note">
          <p>
            <strong>{t('updates.source.other', { name: source.name })}</strong>
          </p>
        </div>
      )}
      <SourceKeys keys={source.keys} />
      <p className={styles.muted}>{t('updates.source.intro')}</p>
      <input
        ref={input}
        type="file"
        accept=".json,application/json"
        hidden
        aria-label={t('updates.source.choose')}
        onChange={(event) => {
          const file = event.target.files?.[0];
          if (file !== undefined) {
            save.mutate(file);
          }
          event.target.value = '';
        }}
      />
      {candidate !== null && (
        <div className={styles.notice} role="status">
          <p>
            <strong>{t('updates.source.saved', { name: candidate.name })}</strong>
          </p>
          <p className={styles.mono}>{candidate.url}</p>
          <SourceKeys keys={candidate.keys} />
          <p className={styles.muted}>
            {t('updates.source.savedBy', {
              user: candidate.savedBy,
              date: new Intl.DateTimeFormat(locale, {
                dateStyle: 'medium',
                timeStyle: 'short',
              }).format(new Date(candidate.savedAt)),
            })}
          </p>
          <p>{t('updates.source.next')}</p>
        </div>
      )}
      <div className={styles.buttons}>
        <Button
          variant="secondary"
          isDisabled={save.isPending}
          onPress={() => input.current?.click()}
        >
          {t('updates.source.choose')}
        </Button>
        {candidate !== null && (
          <Button
            variant="secondary"
            isDisabled={discard.isPending}
            onPress={() => {
              discard.mutate();
            }}
          >
            {t('updates.source.discard')}
          </Button>
        )}
      </div>
      {save.isError && <ErrorNotice error={describeProblem(save.error)} />}
    </section>
  );
}

function InstallDialog({
  app,
  status,
  source,
  onStarted,
  onClose,
}: {
  app: PithyxApp;
  status: UpdateStatus;
  source: 'online' | 'file';
  onStarted: (job: Job) => void;
  onClose: () => void;
}) {
  const { t } = useTranslation('settings');
  const version = (source === 'online' ? status.available?.version : status.staged?.version) ?? '';
  const debianCount = status.debian?.count ?? 0;
  const [includeDebian, setIncludeDebian] = useState(debianCount > 0);
  const install = useMutation({
    mutationFn: () => updatesCall(app, 'internal.admin.updates.install', { source, includeDebian }),
    onSuccess: (job) => {
      onStarted(job);
      onClose();
    },
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
      title={t('updates.installDialog.title', { version })}
      actions={
        <>
          <Button variant="secondary" onPress={onClose}>
            {t('updates.cancel')}
          </Button>
          <Button
            variant="primary"
            isDisabled={install.isPending}
            onPress={() => {
              install.mutate();
            }}
          >
            {t('updates.installDialog.confirm')}
          </Button>
        </>
      }
    >
      <p>{t('updates.installDialog.change', { from: status.installedVersion, to: version })}</p>
      <p>{t('updates.installDialog.snapshot')}</p>
      <p>{t('updates.installDialog.restart')}</p>
      {debianCount > 0 && (
        <Checkbox isSelected={includeDebian} onChange={setIncludeDebian}>
          {t('updates.installDialog.debian', { count: debianCount })}
        </Checkbox>
      )}
      {install.isError && <ErrorNotice error={describeProblem(install.error)} />}
    </Dialog>
  );
}

function RollbackDialog({
  app,
  snapshot,
  onStarted,
  onClose,
}: {
  app: PithyxApp;
  snapshot: SnapshotInfo;
  onStarted: (job: Job) => void;
  onClose: () => void;
}) {
  const { t } = useTranslation('settings');
  const locale = useFormatLocale();
  const [typed, setTyped] = useState('');
  const rollback = useMutation({
    mutationFn: () =>
      updatesCall(app, 'internal.admin.updates.rollback', {
        snapshot: snapshot.name,
        version: typed.trim(),
      }),
    onSuccess: (job) => {
      onStarted(job);
      onClose();
    },
  });
  return (
    <Dialog
      isOpen
      isDismissable
      tone="danger"
      onOpenChange={(open) => {
        if (!open) {
          onClose();
        }
      }}
      title={t('updates.rollbackDialog.title', { version: snapshot.version })}
      actions={
        <>
          <Button variant="secondary" onPress={onClose}>
            {t('updates.cancel')}
          </Button>
          <Button
            variant="danger"
            isDisabled={rollback.isPending || typed.trim() !== snapshot.version}
            onPress={() => {
              rollback.mutate();
            }}
          >
            {t('updates.rollbackDialog.confirm')}
          </Button>
        </>
      }
    >
      <p>
        {t('updates.rollbackDialog.what', {
          version: snapshot.version,
          date: new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeStyle: 'short' }).format(
            new Date(snapshot.createdAt),
          ),
        })}
      </p>
      <p>{t('updates.rollbackDialog.lost')}</p>
      <TextField
        label={t('updates.rollbackDialog.type', { version: snapshot.version })}
        value={typed}
        onChange={setTyped}
        autoComplete="off"
      />
      {rollback.isError && <ErrorNotice error={describeProblem(rollback.error)} />}
    </Dialog>
  );
}
