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
  IconDownload,
  IconKey,
  IconLayoutDashboard,
  IconPencil,
  IconPlugConnected,
  IconPlus,
  IconRestore,
  IconServer,
  IconServer2,
  IconTrash,
  IconUpload,
} from '../../ui/icons';
import type { SdkAppProps } from '../sdk/CoreSdkApp';
import { backupsCall, targetsKey, type BackupJob, type BackupTarget } from '../sdk/backups';
import { ActivityPage } from './ActivityPage';
import { OverviewPage } from './OverviewPage';
import { RestorePage, type RestoreStart } from './RestorePage';
import { useActiveRuns } from './data';
import { ChooserDialog } from './ChooserDialog';
import { DirectionTag, directionOf } from './Direction';
import { DeleteJobDialog, JobDialog, KeyDialog, RevealKeyDialog } from './JobDialogs';
import { JobDetailPage } from './JobDetailPage';
import { EmptyCard } from './EmptyCard';
import { JOB_TONES, JobsPage, jobState, useBackupJobs, useSourceNames } from './JobsPage';
import { PullDialog } from './PullDialog';
import { describeProblem, formatDate } from '../storage/shared';
import {
  AddTargetDialog,
  DeleteTargetDialog,
  EditTargetDialog,
  HostKeyDialog,
  PublicKey,
  useReplaceTarget,
  type Purpose,
} from './TargetDialogs';
import styles from './Backup.module.css';

type Page = 'overview' | 'activity' | 'restore' | 'jobs' | 'targets' | 'pulls' | 'servers';

const PAGES: readonly Page[] = [
  'overview',
  'activity',
  'restore',
  'jobs',
  'targets',
  'pulls',
  'servers',
];

type Opened =
  | { kind: 'add'; purpose: Purpose }
  | { kind: 'edit' | 'hostKey' | 'delete'; target: BackupTarget }
  | { kind: 'choose' }
  | { kind: 'addJob' | 'addPull' }
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
 * The Backup app (D414, D415, D416, D420): the jobs that back up shared folders and homes with restic from ZFS
 * snapshots, the targets they go to, each a server reached over SSH with its own key and the host key the
 * administrator confirmed, the server backups that pull Linux servers onto this box with rsync (#396), and the
 * activity of every run. The navigation groups the pages by direction, so it is always clear which way a backup goes,
 * and restoring files and folders from any version (#395). Administrators only.
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
  const allTargets = targets.data?.targets;
  // Targets take box backups, servers are pulled onto the box; each has its own page.
  const targetList = allTargets?.filter((target) => target.purpose === 'target');
  const serverList = allTargets?.filter((target) => target.purpose === 'server');
  const jobs = useBackupJobs(app);
  const { sourcePath } = useSourceNames(app);
  const running = [...useActiveRuns().values()].filter((job) => job.state === 'running').length;
  const addTarget = () => {
    setOpened({ kind: 'add', purpose: 'target' });
  };
  const addServer = () => {
    setOpened({ kind: 'add', purpose: 'server' });
  };
  // A backup needs a target and a server backup a server; without one the way leads to adding it first.
  const addJob = () => {
    if (targetList?.length === 0) {
      setPage('targets');
      setShownId(null);
      addTarget();
    } else {
      setOpened({ kind: 'addJob' });
    }
  };
  const addPull = () => {
    if (serverList?.length === 0) {
      setPage('servers');
      setShownId(null);
      addServer();
    } else {
      setOpened({ kind: 'addPull' });
    }
  };
  // The primary action of the targets and servers sits in the toolbar only beside a list; an empty list offers it in
  // its own place. The jobs pages have their own toolbar, which also acts on the selected job.
  const toolbarAction =
    page === 'targets' && targetList !== undefined && targetList.length > 0
      ? { label: t('targets.add'), toolbar: t('targets.toolbar'), onPress: addTarget }
      : page === 'servers' && serverList !== undefined && serverList.length > 0
        ? { label: t('servers.add'), toolbar: t('servers.toolbar'), onPress: addServer }
        : undefined;
  const countOf = (kind: BackupJob['kind']) =>
    jobs.data?.jobs.filter((job) => job.kind === kind).length;
  const jobCount = countOf('versioned');
  const pullCount = countOf('pull');
  // A deleted backup closes its page.
  const shown =
    page === 'jobs' || page === 'pulls'
      ? jobs.data?.jobs.find((job) => job.id === shownId)
      : undefined;
  const targetOf = (job: BackupJob) => allTargets?.find((target) => target.id === job.targetId);
  const destinationOf = (job: BackupJob) =>
    job.pull === null ? '' : sourcePath({ shareId: job.pull.shareId, path: job.pull.folder });
  const shownState = shown === undefined ? undefined : jobState(shown);
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
    {
      id: 'in',
      label: t('nav.in'),
      items: [
        {
          id: 'pulls',
          label: t('nav.pulls'),
          icon: <IconDownload size={18} stroke={1.75} aria-hidden />,
          ...(pullCount === undefined ? {} : { count: pullCount }),
        },
        {
          id: 'servers',
          label: t('nav.servers'),
          icon: <IconServer2 size={18} stroke={1.75} aria-hidden />,
          ...(serverList === undefined ? {} : { count: serverList.length }),
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
              <DirectionTag direction={directionOf(shown.kind)} />
              <StatusBadge tone={JOB_TONES[shownState]}>
                {t(`jobs.status.${shownState}`)}
              </StatusBadge>
            </span>
          ) : page === 'overview' || page === 'activity' || page === 'restore' ? (
            t(`${page}.title`)
          ) : (
            <span className={styles.title}>
              {t(`${page}.title`)}
              <DirectionTag direction={page === 'pulls' || page === 'servers' ? 'in' : 'out'} />
            </span>
          )
        }
        toolbar={
          toolbarAction === undefined ? undefined : (
            <Toolbar aria-label={toolbarAction.toolbar}>
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
              setOpened({ kind: 'choose' });
            }}
            onShowPage={setPage}
            onCheckHostKey={(target) => {
              setOpened({ kind: 'hostKey', target });
            }}
          />
        ) : page === 'activity' ? (
          <ActivityPage app={app} targets={allTargets} />
        ) : page === 'restore' ? (
          <RestorePage
            key={
              restoreStart === null
                ? 'restore'
                : `${String(restoreStart.jobId)}/${String(restoreStart.versionId)}`
            }
            app={app}
            // Server backups are restored from their snapshots in Files for now.
            jobs={jobs.data?.jobs.filter((job) => job.kind !== 'pull')}
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
        ) : page === 'jobs' || page === 'pulls' ? (
          <JobsPage
            key={page}
            app={app}
            kind={page === 'pulls' ? 'pull' : 'versioned'}
            jobs={jobs}
            targets={page === 'pulls' ? serverList : targetList}
            onAdd={page === 'pulls' ? addPull : addJob}
            onAddTarget={() => {
              if (page === 'pulls') {
                setPage('servers');
                addServer();
              } else {
                setPage('targets');
                addTarget();
              }
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
            key={page}
            app={app}
            purpose={page === 'servers' ? 'server' : 'target'}
            targets={targets}
            onAdd={page === 'servers' ? addServer : addTarget}
            onOpen={(kind, target) => {
              setOpened({ kind, target });
            }}
          />
        )}
      </AppFrame>
      {opened?.kind === 'add' && (
        <AddTargetDialog app={app} purpose={opened.purpose} onClose={close} />
      )}
      {opened?.kind === 'choose' && (
        <ChooserDialog
          onClose={close}
          onChoose={(direction) => {
            setOpened(null);
            setShownId(null);
            if (direction === 'in') {
              setPage('pulls');
              addPull();
            } else {
              setPage('jobs');
              addJob();
            }
          }}
        />
      )}
      {opened?.kind === 'edit' && (
        <EditTargetDialog app={app} target={opened.target} onClose={close} />
      )}
      {opened?.kind === 'hostKey' && (
        <HostKeyDialog app={app} target={opened.target} onClose={close} />
      )}
      {opened?.kind === 'delete' && (
        <DeleteTargetDialog app={app} target={opened.target} onClose={close} />
      )}
      {(opened?.kind === 'addPull' ||
        (opened?.kind === 'editJob' && opened.job.kind === 'pull')) && (
        <PullDialog
          app={app}
          {...(opened.kind === 'editJob'
            ? { job: opened.job, destination: destinationOf(opened.job) }
            : {})}
          servers={serverList ?? []}
          onClose={close}
        />
      )}
      {(opened?.kind === 'addJob' ||
        (opened?.kind === 'editJob' && opened.job.kind !== 'pull')) && (
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
        <DeleteJobDialog
          app={app}
          job={opened.job}
          destination={destinationOf(opened.job)}
          onClose={close}
        />
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

/** The targets of box backups, or with purpose server the servers of server backups (#396). */
function TargetsPage({
  app,
  purpose,
  targets,
  onAdd,
  onOpen,
}: {
  app: PithyxApp;
  purpose: Purpose;
  targets: ReturnType<typeof useTargets>;
  onAdd: () => void;
  onOpen: (kind: 'edit' | 'hostKey' | 'delete', target: BackupTarget) => void;
}) {
  const { t } = useTranslation('backup');
  const texts = purpose === 'server' ? 'servers' : 'targets';
  const list = targets.data?.targets.filter((target) => target.purpose === purpose);
  return (
    <div className={styles.page}>
      <p className={styles.intro}>{t(`${texts}.intro`)}</p>
      {targets.isError && <ErrorNotice error={describeProblem(targets.error)} />}
      {targets.isPending && <p className={styles.muted}>{t(`${texts}.loading`)}</p>}
      {list?.length === 0 && (
        <EmptyCard
          icon={
            purpose === 'server' ? (
              <IconServer2 size={24} stroke={1.75} />
            ) : (
              <IconServer size={24} stroke={1.75} />
            )
          }
          title={t(`${texts}.emptyTitle`)}
          text={t(`${texts}.empty`)}
          action={t(`${texts}.add`)}
          onAction={onAdd}
        />
      )}
      {list !== undefined && list.length > 0 && (
        <ul className={styles.targets} aria-label={t(`${texts}.list`)}>
          {list.map((target) => (
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
    target.purpose === 'server'
      ? t('servers.readsBelow', { path: target.path })
      : target.path === ''
        ? t('targets.loginFolder')
        : t('targets.folder', { path: target.path }),
    ...(last === null ? [] : [t('targets.tested', { time: formatDate(last.testedAt, locale) })]),
  ].join(' · ');
  const hostKeyChanged = last?.errorCode === 'backup.target.host_key_changed';

  return (
    <Card
      icon={
        target.purpose === 'server' ? (
          <IconServer2 size={24} stroke={1.75} aria-hidden />
        ) : (
          <IconServer size={24} stroke={1.75} aria-hidden />
        )
      }
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
        <PublicKey app={app} target={target} />
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
