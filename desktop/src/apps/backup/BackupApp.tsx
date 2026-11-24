import type { PithyxApp } from '@pithyx/sdk';
import {
  AppFrame,
  Button,
  Card,
  NavList,
  StatusBadge,
  Toolbar,
  type AppFrameView,
  type NavGroup,
  type StatusTone,
} from '@pithyx/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { useFormatLocale } from '../../i18n';
import {
  IconActivity,
  IconKey,
  IconLayoutDashboard,
  IconPencil,
  IconPlugConnected,
  IconPlus,
  IconRestore,
  IconServer,
  IconTrash,
  IconUpload,
} from '../../ui/icons';
import type { SdkAppProps } from '../sdk/CoreSdkApp';
import { backupsCall, targetsKey, type BackupJob, type BackupTarget } from '../sdk/backups';
import { ActivityPage } from './ActivityPage';
import { OverviewPage } from './OverviewPage';
import { RestorePage, type RestoreStart } from './RestorePage';
import { useActiveRuns } from './data';
import { DirectionTag } from './Direction';
import { DeleteJobDialog, JobDialog, KeyDialog, RevealKeyDialog } from './JobDialogs';
import { JobDetailPage } from './JobDetailPage';
import { EmptyCard } from './EmptyCard';
import { JOB_TONES, JobsPage, jobState, useBackupJobs } from './JobsPage';
import { describeProblem, formatDate } from '../storage/shared';
import {
  AddTargetDialog,
  DeleteTargetDialog,
  EditTargetDialog,
  HostKeyDialog,
  PublicKey,
  useReplaceTarget,
} from './TargetDialogs';
import styles from './Backup.module.css';

type Page = 'overview' | 'activity' | 'restore' | 'jobs' | 'targets';

const PAGES: readonly Page[] = ['overview', 'activity', 'restore', 'jobs', 'targets'];

type Opened =
  | { kind: 'add' }
  | { kind: 'edit' | 'hostKey' | 'delete'; target: BackupTarget }
  | { kind: 'addJob' }
  | { kind: 'editJob' | 'deleteJob'; job: BackupJob }
  | { kind: 'key'; job: BackupJob; key: string }
  | { kind: 'revealKey'; job: BackupJob }
  | null;

/** Every backup target; the Backup app's calls replace a changed target in it. */
function useTargets(app: PithyxApp) {
  return useQuery({
    queryKey: targetsKey,
    queryFn: () => backupsCall(app, 'internal.admin.backups.targets', {}),
  });
}

/**
 * The Backup app (D414, D415, D416): the jobs that back up shared folders and homes with restic from ZFS snapshots, the
 * targets they go to, each a server reached over SSH with its own key and the host key the administrator confirmed,
 * and the activity of every run. The navigation groups the pages by direction, so it is always clear which way a
 * backup goes, and restoring files and folders from any version (#395). Administrators only.
 */
export function BackupApp({ app }: SdkAppProps) {
  const { t } = useTranslation('backup');
  const targets = useTargets(app);
  const [page, setPage] = useState<Page>('overview');
  const [view, setView] = useState<AppFrameView>('page');
  const [opened, setOpened] = useState<Opened>(null);
  // The backup whose own page is open over the box backups.
  const [shownId, setShownId] = useState<number | null>(null);
  // Where the restore page starts when a backup's version opened it; a new start shows a fresh page.
  const [restoreStart, setRestoreStart] = useState<RestoreStart | null>(null);
  const close = () => {
    setOpened(null);
  };
  const targetList = targets.data?.targets;
  const jobs = useBackupJobs(app);
  const running = [...useActiveRuns().values()].filter((job) => job.state === 'running').length;
  const addTarget = () => {
    setOpened({ kind: 'add' });
  };
  const addJob = () => {
    setOpened({ kind: 'addJob' });
  };
  // The primary action of the targets sits in the toolbar only beside a list; an empty list offers it in its own place.
  // The jobs page has its own toolbar, which also acts on the selected job.
  const toolbarAction =
    page === 'targets' && targetList !== undefined && targetList.length > 0
      ? { label: t('targets.add'), onPress: addTarget }
      : undefined;
  const jobCount = jobs.data?.jobs.length;
  // A deleted backup closes its page.
  const shown = page === 'jobs' ? jobs.data?.jobs.find((job) => job.id === shownId) : undefined;
  const targetOf = (job: BackupJob) => targetList?.find((target) => target.id === job.targetId);
  const shownState = shown === undefined ? undefined : jobState(shown);
  // "Onto this box" (servers backed up to the box) joins with the pulls of #396.
  const groups: NavGroup[] = [
    {
      id: 'overview',
      label: t('nav.overview'),
      items: [
        {
          id: 'overview',
          label: t('nav.overviewPage'),
          icon: <IconLayoutDashboard size={18} stroke={1.75} aria-hidden />,
        },
        {
          id: 'activity',
          label: t('nav.activity'),
          icon: <IconActivity size={18} stroke={1.75} aria-hidden />,
          ...(running === 0
            ? {}
            : { count: running, countLabel: t('nav.runningCount', { count: running }) }),
        },
        {
          id: 'restore',
          label: t('nav.restore'),
          icon: <IconRestore size={18} stroke={1.75} aria-hidden />,
        },
      ],
    },
    {
      id: 'out',
      label: t('nav.out'),
      items: [
        {
          id: 'jobs',
          label: t('nav.jobs'),
          icon: <IconUpload size={18} stroke={1.75} aria-hidden />,
          ...(jobCount === undefined ? {} : { count: jobCount }),
        },
        {
          id: 'targets',
          label: t('nav.targets'),
          icon: <IconServer size={18} stroke={1.75} aria-hidden />,
          ...(targetList === undefined ? {} : { count: targetList.length }),
        },
      ],
    },
  ];

  return (
    <>
      <AppFrame
        view={view}
        onViewChange={setView}
        pageKey={shown === undefined ? page : `job-${String(shown.id)}`}
        navigation={
          <NavList
            aria-label={t('nav.label')}
            searchable={false}
            groups={groups}
            selectedId={page}
            onSelect={(id) => {
              setPage(PAGES.find((entry) => entry === id) ?? 'overview');
              setShownId(null);
              setRestoreStart(null);
              setView('page');
            }}
          />
        }
        title={
          shown !== undefined && shownState !== undefined ? (
            <span className={styles.title}>
              {shown.settings.name}
              <DirectionTag direction="out" />
              <StatusBadge tone={JOB_TONES[shownState]}>
                {t(`jobs.status.${shownState}`)}
              </StatusBadge>
            </span>
          ) : page === 'overview' || page === 'activity' || page === 'restore' ? (
            t(`${page}.title`)
          ) : (
            <span className={styles.title}>
              {t(`${page}.title`)}
              <DirectionTag direction="out" />
            </span>
          )
        }
        toolbar={
          toolbarAction === undefined ? undefined : (
            <Toolbar aria-label={t('targets.toolbar')}>
              <Button variant="primary" onPress={toolbarAction.onPress}>
                <IconPlus size={18} stroke={1.75} aria-hidden />
                {toolbarAction.label}
              </Button>
            </Toolbar>
          )
        }
      >
        {page === 'overview' ? (
          <OverviewPage
            app={app}
            targets={targetList}
            onAddJob={() => {
              // A backup needs a target; without one the overview leads to adding it first.
              if (targetList?.length === 0) {
                setPage('targets');
                addTarget();
              } else {
                addJob();
              }
            }}
            onShowPage={setPage}
            onCheckHostKey={(target) => {
              setOpened({ kind: 'hostKey', target });
            }}
          />
        ) : page === 'activity' ? (
          <ActivityPage app={app} targets={targetList} />
        ) : page === 'restore' ? (
          <RestorePage
            key={
              restoreStart === null
                ? 'restore'
                : `${String(restoreStart.jobId)}/${String(restoreStart.versionId)}`
            }
            app={app}
            jobs={jobs.data?.jobs}
            start={restoreStart}
            onAddJob={() => {
              setPage('jobs');
              addJob();
            }}
          />
        ) : shown !== undefined ? (
          <JobDetailPage
            app={app}
            job={shown}
            target={targetOf(shown)}
            onBack={() => {
              setShownId(null);
            }}
            onBrowse={(version) => {
              setRestoreStart({ jobId: shown.id, versionId: version.id });
              setShownId(null);
              setPage('restore');
            }}
            onOpen={(kind) => {
              setOpened({
                kind: kind === 'edit' ? 'editJob' : kind === 'delete' ? 'deleteJob' : 'revealKey',
                job: shown,
              });
            }}
          />
        ) : page === 'jobs' ? (
          <JobsPage
            app={app}
            jobs={jobs}
            targets={targetList}
            onAdd={addJob}
            onAddTarget={() => {
              setPage('targets');
              addTarget();
            }}
            onOpen={(kind, job) => {
              setOpened({ kind: kind === 'edit' ? 'editJob' : 'deleteJob', job });
            }}
            onShow={(job) => {
              setShownId(job.id);
            }}
          />
        ) : (
          <TargetsPage
            app={app}
            targets={targets}
            onAdd={addTarget}
            onOpen={(kind, target) => {
              setOpened({ kind, target });
            }}
          />
        )}
      </AppFrame>
      {opened?.kind === 'add' && <AddTargetDialog app={app} onClose={close} />}
      {opened?.kind === 'edit' && (
        <EditTargetDialog app={app} target={opened.target} onClose={close} />
      )}
      {opened?.kind === 'hostKey' && (
        <HostKeyDialog app={app} target={opened.target} onClose={close} />
      )}
      {opened?.kind === 'delete' && (
        <DeleteTargetDialog app={app} target={opened.target} onClose={close} />
      )}
      {(opened?.kind === 'addJob' || opened?.kind === 'editJob') && (
        <JobDialog
          app={app}
          {...(opened.kind === 'editJob' ? { job: opened.job } : {})}
          targets={targetList ?? []}
          onClose={close}
          onCreated={(job, key) => {
            setOpened({ kind: 'key', job, key });
          }}
        />
      )}
      {opened?.kind === 'deleteJob' && (
        <DeleteJobDialog app={app} job={opened.job} onClose={close} />
      )}
      {opened?.kind === 'key' && (
        <KeyDialog
          job={opened.job}
          repositoryKey={opened.key}
          targetName={targetOf(opened.job)?.name ?? ''}
          onClose={close}
        />
      )}
      {opened?.kind === 'revealKey' && (
        <RevealKeyDialog
          app={app}
          job={opened.job}
          targetName={targetOf(opened.job)?.name ?? ''}
          onClose={close}
        />
      )}
    </>
  );
}

function TargetsPage({
  app,
  targets,
  onAdd,
  onOpen,
}: {
  app: PithyxApp;
  targets: ReturnType<typeof useTargets>;
  onAdd: () => void;
  onOpen: (kind: 'edit' | 'hostKey' | 'delete', target: BackupTarget) => void;
}) {
  const { t } = useTranslation('backup');
  return (
    <div className={styles.page}>
      <p className={styles.intro}>{t('targets.intro')}</p>
      {targets.isError && <ErrorNotice error={describeProblem(targets.error)} />}
      {targets.isPending && <p className={styles.muted}>{t('targets.loading')}</p>}
      {targets.data?.targets.length === 0 && (
        <EmptyCard
          icon={<IconServer size={24} stroke={1.75} />}
          title={t('targets.emptyTitle')}
          text={t('targets.empty')}
          action={t('targets.add')}
          onAction={onAdd}
        />
      )}
      {targets.data !== undefined && targets.data.targets.length > 0 && (
        <ul className={styles.targets} aria-label={t('targets.list')}>
          {targets.data.targets.map((target) => (
            <li key={target.id}>
              <TargetCard
                app={app}
                target={target}
                onOpen={(kind) => {
                  onOpen(kind, target);
                }}
              />
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

type TestState = 'ok' | 'failed' | 'untested';

const STATUS_TONES = {
  ok: 'success',
  failed: 'danger',
  untested: 'neutral',
} as const satisfies Record<TestState, StatusTone>;

export function testState(target: BackupTarget): TestState {
  if (target.lastTest === null) {
    return 'untested';
  }
  return target.lastTest.succeeded ? 'ok' : 'failed';
}

function TargetCard({
  app,
  target,
  onOpen,
}: {
  app: PithyxApp;
  target: BackupTarget;
  onOpen: (kind: 'edit' | 'hostKey' | 'delete') => void;
}) {
  const { t } = useTranslation('backup');
  const locale = useFormatLocale();
  const queryClient = useQueryClient();
  const replace = useReplaceTarget();
  const test = useMutation({
    mutationFn: () => backupsCall(app, 'internal.admin.backups.test', { id: target.id }),
    onSuccess: replace,
    // A target removed elsewhere leaves the list.
    onError: () => queryClient.invalidateQueries({ queryKey: targetsKey }),
  });
  const state = testState(target);
  const last = target.lastTest;
  const facts = [
    t('targets.address', { user: target.user, host: target.host, port: target.port }),
    target.path === '' ? t('targets.loginFolder') : t('targets.folder', { path: target.path }),
    ...(last === null ? [] : [t('targets.tested', { time: formatDate(last.testedAt, locale) })]),
  ].join(' · ');
  const hostKeyChanged = last?.errorCode === 'backup.target.host_key_changed';

  return (
    <Card
      icon={<IconServer size={24} stroke={1.75} aria-hidden />}
      title={target.name}
      badge={<StatusBadge tone={STATUS_TONES[state]}>{t(`targets.status.${state}`)}</StatusBadge>}
      facts={facts}
      defaultExpanded={state !== 'ok'}
      actions={
        <>
          <Button
            size="sm"
            isDisabled={test.isPending}
            onPress={() => {
              test.mutate();
            }}
          >
            <IconPlugConnected size={16} stroke={1.75} aria-hidden />
            {t('targets.test')}
          </Button>
          {hostKeyChanged && (
            <Button
              size="sm"
              onPress={() => {
                onOpen('hostKey');
              }}
            >
              <IconKey size={16} stroke={1.75} aria-hidden />
              {t('targets.checkHostKey')}
            </Button>
          )}
          <Button
            size="sm"
            onPress={() => {
              onOpen('edit');
            }}
          >
            <IconPencil size={16} stroke={1.75} aria-hidden />
            {t('targets.edit')}
          </Button>
          <Button
            size="sm"
            variant="danger"
            onPress={() => {
              onOpen('delete');
            }}
          >
            <IconTrash size={16} stroke={1.75} aria-hidden />
            {t('targets.delete')}
          </Button>
        </>
      }
    >
      <div className={styles.cardBody}>
        {last !== null && !last.succeeded && last.errorCode !== null && (
          <ErrorNotice
            error={describeProblem({
              code: last.errorCode,
              params: (last.errorParams ?? {}) as Record<string, unknown>,
              requestId: '',
            })}
          />
        )}
        {test.isError && <ErrorNotice error={describeProblem(test.error)} />}
        <PublicKey app={app} publicKey={target.publicKey} />
        <div className={styles.field}>
          <span className={styles.fieldLabel}>{t('targets.hostKey')}</span>
          <span className={`${styles.mono} ${styles.breakable}`}>
            {t('targets.hostKeyValue', {
              type: target.hostKey.type,
              fingerprint: target.hostKey.fingerprint,
            })}
          </span>
        </div>
      </div>
    </Card>
  );
}
