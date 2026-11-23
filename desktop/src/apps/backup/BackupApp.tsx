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
  IconArchive,
  IconKey,
  IconPencil,
  IconPlugConnected,
  IconPlus,
  IconServer,
  IconTrash,
} from '../../ui/icons';
import type { SdkAppProps } from '../sdk/CoreSdkApp';
import { backupsCall, targetsKey, type BackupJob, type BackupTarget } from '../sdk/backups';
import { DeleteJobDialog, JobDialog, KeyDialog } from './JobDialogs';
import { EmptyCard } from './EmptyCard';
import { JobsPage, useBackupJobs } from './JobsPage';
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

type Page = 'jobs' | 'targets';

type Opened =
  | { kind: 'add' }
  | { kind: 'edit' | 'hostKey' | 'delete'; target: BackupTarget }
  | { kind: 'addJob' }
  | { kind: 'editJob' | 'deleteJob'; job: BackupJob }
  | { kind: 'key'; job: BackupJob; key: string }
  | null;

/** Every backup target; the Backup app's calls replace a changed target in it. */
function useTargets(app: PithyxApp) {
  return useQuery({
    queryKey: targetsKey,
    queryFn: () => backupsCall(app, 'internal.admin.backups.targets', {}),
  });
}

/**
 * The Backup app (D414, D415): the jobs that back up shared folders and homes with restic from ZFS snapshots, and the
 * targets they go to, each a server reached over SSH with its own key and the host key the administrator confirmed.
 * Administrators only; restores follow (#395).
 */
export function BackupApp({ app }: SdkAppProps) {
  const { t } = useTranslation('backup');
  const targets = useTargets(app);
  const [page, setPage] = useState<Page>('jobs');
  const [view, setView] = useState<AppFrameView>('page');
  const [opened, setOpened] = useState<Opened>(null);
  const close = () => {
    setOpened(null);
  };
  const targetList = targets.data?.targets;
  const jobs = useBackupJobs(app);
  const addTarget = () => {
    setOpened({ kind: 'add' });
  };
  const addJob = () => {
    setOpened({ kind: 'addJob' });
  };
  // The primary action sits in the toolbar only beside a list; an empty list offers it in its own place.
  const toolbarAction =
    page === 'targets'
      ? targetList !== undefined && targetList.length > 0
        ? { label: t('targets.add'), onPress: addTarget }
        : undefined
      : jobs.data !== undefined && jobs.data.jobs.length > 0
        ? { label: t('jobs.add'), onPress: addJob }
        : undefined;
  const groups: NavGroup[] = [
    {
      id: 'backup',
      items: [
        {
          id: 'jobs',
          label: t('nav.jobs'),
          icon: <IconArchive size={18} stroke={1.75} aria-hidden />,
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
        pageKey={page}
        navigation={
          <NavList
            aria-label={t('nav.label')}
            searchable={false}
            groups={groups}
            selectedId={page}
            onSelect={(id) => {
              setPage(id === 'targets' ? 'targets' : 'jobs');
              setView('page');
            }}
          />
        }
        title={t(`${page}.title`)}
        toolbar={
          toolbarAction === undefined ? undefined : (
            <Toolbar aria-label={t(`${page}.toolbar`)}>
              <Button variant="primary" onPress={toolbarAction.onPress}>
                <IconPlus size={18} stroke={1.75} aria-hidden />
                {toolbarAction.label}
              </Button>
            </Toolbar>
          )
        }
      >
        {page === 'jobs' ? (
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
          targetName={targetList?.find((target) => target.id === opened.job.targetId)?.name ?? ''}
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
