import type { PithyxApp } from '@pithyx/sdk';
import { AppFrame, Button, Card, StatusBadge, Toolbar, type StatusTone } from '@pithyx/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { useFormatLocale } from '../../i18n';
import {
  IconKey,
  IconPencil,
  IconPlugConnected,
  IconPlus,
  IconServer,
  IconTrash,
} from '../../ui/icons';
import type { SdkAppProps } from '../sdk/CoreSdkApp';
import { backupsCall, targetsKey, type BackupTarget } from '../sdk/backups';
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

type Opened =
  { kind: 'add' } | { kind: 'edit' | 'hostKey' | 'delete'; target: BackupTarget } | null;

/** Every backup target; the Backup app's calls replace a changed target in it. */
function useTargets(app: PithyxApp) {
  return useQuery({
    queryKey: targetsKey,
    queryFn: () => backupsCall(app, 'internal.admin.backups.targets', {}),
  });
}

/**
 * The Backup app (D414): the targets backups go to, each a server reached over SSH with its own key and the host key
 * the administrator confirmed. Administrators only; backup jobs, runs and restores follow (#394, #395).
 */
export function BackupApp({ app }: SdkAppProps) {
  const { t } = useTranslation('backup');
  const targets = useTargets(app);
  const [opened, setOpened] = useState<Opened>(null);
  const close = () => {
    setOpened(null);
  };
  const add = () => {
    setOpened({ kind: 'add' });
  };

  return (
    <>
      <AppFrame
        title={t('targets.title')}
        toolbar={
          <Toolbar aria-label={t('targets.toolbar')}>
            <Button variant="primary" onPress={add}>
              <IconPlus size={18} stroke={1.75} aria-hidden />
              {t('targets.add')}
            </Button>
          </Toolbar>
        }
      >
        <div className={styles.page}>
          <p className={styles.muted}>{t('targets.intro')}</p>
          {targets.isError && <ErrorNotice error={describeProblem(targets.error)} />}
          {targets.isPending && <p className={styles.muted}>{t('targets.loading')}</p>}
          {targets.data?.targets.length === 0 && (
            <div className={styles.empty}>
              <p className={styles.muted}>{t('targets.empty')}</p>
              <Button onPress={add}>
                <IconPlus size={16} stroke={1.75} aria-hidden />
                {t('targets.add')}
              </Button>
            </div>
          )}
          {targets.data !== undefined && targets.data.targets.length > 0 && (
            <div className={styles.targets} role="list" aria-label={t('targets.list')}>
              {targets.data.targets.map((target) => (
                <div key={target.id} role="listitem">
                  <TargetCard
                    app={app}
                    target={target}
                    onOpen={(kind) => {
                      setOpened({ kind, target });
                    }}
                  />
                </div>
              ))}
            </div>
          )}
        </div>
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
    </>
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
    </Card>
  );
}
