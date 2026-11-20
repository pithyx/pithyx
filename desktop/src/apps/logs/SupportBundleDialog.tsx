import type { PithyxApp } from '@pithyx/sdk';
import { Button, Checkbox, Dialog, PasswordField, ProgressBar, TextField } from '@pithyx/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { formatBytes } from '../../desktop/format';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { useFormatLocale } from '../../i18n';
import {
  describeProblem,
  logsCall,
  onSupportJob,
  SUPPORT_PARTS,
  type Job,
  type SupportBundlePart,
} from '../sdk/logs';
import styles from './Logs.module.css';

const stateKey = ['logs', 'support'] as const;

/** Every part but the memory dump, which holds secrets and is only ever chosen by hand. */
const DEFAULT_PARTS = SUPPORT_PARTS.filter((part) => part !== 'memoryDump');

/**
 * The support bundle (D412): the parts to put in, then the job's progress, then the file to download. The box keeps
 * one bundle for an hour; making a new one replaces it.
 */
export function SupportBundleDialog({ app, onClose }: { app: PithyxApp; onClose: () => void }) {
  const { t } = useTranslation('logs');
  const locale = useFormatLocale();
  const client = useQueryClient();
  const twoFactor = app.context.user.twoFactor;
  const [parts, setParts] = useState<SupportBundlePart[]>(DEFAULT_PARTS);
  const [password, setPassword] = useState('');
  const [code, setCode] = useState('');
  const [job, setJob] = useState<Job | null>(null);
  const state = useQuery({
    queryKey: stateKey,
    queryFn: () => logsCall(app, 'internal.admin.logs.support', {}),
  });
  const running = state.data?.jobId ?? null;
  const create = useMutation({
    mutationFn: () =>
      logsCall(app, 'internal.admin.logs.support.create', {
        parts,
        ...(parts.includes('memoryDump')
          ? { password, ...(twoFactor ? { totpCode: code.trim() } : {}) }
          : {}),
      }),
    onSuccess: (started) => {
      setJob(started);
      setPassword('');
      setCode('');
    },
  });
  const remove = useMutation({
    mutationFn: (id: string) => logsCall(app, 'internal.admin.logs.support.delete', { id }),
    onSettled: () => client.invalidateQueries({ queryKey: stateKey }),
  });

  // The job's changes come over the WebSocket: syncing with the host's event.
  useEffect(
    () =>
      onSupportJob(app, (next) => {
        setJob(next);
        if (next.state === 'succeeded' || next.state === 'failed' || next.state === 'cancelled') {
          void client.invalidateQueries({ queryKey: stateKey });
        }
      }),
    [app, client],
  );
  // A bundle started before the dialog opened: follow its job too.
  useEffect(() => {
    if (running !== null) {
      void logsCall(app, 'internal.admin.logs.support.follow', { jobId: running });
    }
  }, [app, running]);

  const working =
    create.isPending ||
    (job !== null && (job.state === 'queued' || job.state === 'running')) ||
    (job === null && running !== null);
  const dump = parts.includes('memoryDump');
  const ready =
    parts.length > 0 && (!dump || (password !== '' && (!twoFactor || code.trim() !== '')));
  const bundle = state.data?.bundle ?? null;
  const toggle = (part: SupportBundlePart, selected: boolean) => {
    setParts(
      SUPPORT_PARTS.filter((candidate) =>
        candidate === part ? selected : parts.includes(candidate),
      ),
    );
  };
  const time = (value: string) =>
    new Intl.DateTimeFormat(locale, { timeStyle: 'short' }).format(new Date(value));

  return (
    <Dialog
      isOpen
      isDismissable
      onOpenChange={(open) => {
        if (!open) {
          onClose();
        }
      }}
      title={t('support.title')}
      actions={
        <>
          <Button variant="secondary" onPress={onClose}>
            {t('support.close')}
          </Button>
          <Button
            variant={bundle === null ? 'primary' : 'secondary'}
            onPress={() => {
              create.mutate();
            }}
            isDisabled={!ready || working}
          >
            {bundle === null ? t('support.create') : t('support.createAgain')}
          </Button>
          {bundle !== null && (
            <Button
              variant="primary"
              onPress={() => {
                void logsCall(app, 'internal.admin.logs.support.download', { id: bundle.id });
              }}
            >
              {t('support.download')}
            </Button>
          )}
        </>
      }
    >
      <div className={styles.form}>
        <p className={styles.muted}>{t('support.intro')}</p>
        <fieldset className={styles.group} disabled={working}>
          <legend className={styles.groupTitle}>{t('support.parts')}</legend>
          {SUPPORT_PARTS.map((part) => (
            <div key={part} className={styles.part}>
              <Checkbox
                isSelected={parts.includes(part)}
                isDisabled={working}
                onChange={(selected) => {
                  toggle(part, selected);
                }}
              >
                {t(`support.part.${part}.label`)}
              </Checkbox>
              <p className={styles.partHint}>{t(`support.part.${part}.hint`)}</p>
            </div>
          ))}
        </fieldset>
        {dump && (
          <div className={styles.warning} role="note">
            <p className={styles.warningText}>{t('support.dumpWarning')}</p>
            <p className={styles.muted}>{t('support.stepUp', { twoFactor: String(twoFactor) })}</p>
            <PasswordField
              label={t('support.password')}
              value={password}
              onChange={setPassword}
              autoComplete="current-password"
            />
            {twoFactor && (
              <TextField
                label={t('support.code')}
                value={code}
                onChange={setCode}
                inputMode="numeric"
                autoComplete="one-time-code"
              />
            )}
          </div>
        )}
        {working && (
          <ProgressBar
            label={
              job?.progress.currentItem != null
                ? t(`support.part.${job.progress.currentItem as SupportBundlePart}.label`)
                : t('support.working')
            }
            {...(job !== null && job.progress.itemsTotal > 0
              ? { value: job.progress.itemsDone, maxValue: job.progress.itemsTotal }
              : { isIndeterminate: true })}
          />
        )}
        {job?.state === 'failed' && job.errorCode !== null && (
          <ErrorNotice
            error={describeProblem({
              code: job.errorCode,
              params: job.errorParams ?? {},
              requestId: job.requestId,
            })}
          />
        )}
        {create.isError && <ErrorNotice error={describeProblem(create.error)} />}
        {state.isError && <ErrorNotice error={describeProblem(state.error)} />}
        {bundle !== null && !working && (
          <div className={styles.bundle} role="status">
            <p className={styles.bundleName}>{bundle.fileName}</p>
            <p className={styles.muted}>
              {t('support.ready', {
                size: formatBytes(bundle.sizeBytes, locale),
                expires: time(bundle.expiresAt),
              })}
            </p>
            {bundle.failures > 0 && (
              <p className={styles.muted}>{t('support.failures', { count: bundle.failures })}</p>
            )}
            <p className={styles.muted}>{t('support.share')}</p>
            <div>
              <Button
                onPress={() => {
                  remove.mutate(bundle.id);
                }}
                isDisabled={remove.isPending}
              >
                {t('support.delete')}
              </Button>
            </div>
          </div>
        )}
        {remove.isError && <ErrorNotice error={describeProblem(remove.error)} />}
      </div>
    </Dialog>
  );
}
