import type { PithyxApp } from '@pithyx/sdk';
import { useMutation } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { Badge, Button, Dialog, TextField } from '@pithyx/ui';
import {
  internalCall,
  type ImportablePool,
  type ImportVdev,
  type StorageDisk,
} from '../sdk/internal';
import {
  ConfirmNameField,
  isFinished,
  JobStatus,
  PlanPreview,
  RunButton,
  RunError,
  useRunPlan,
} from './PlanRun';
import styles from './Storage.module.css';
import { describeProblem, guardReason, stateKey, stateTone } from './shared';

/**
 * Importing a pool that is on the disks but not imported: scan only when asked (a scan reads every disk's labels), pick
 * a pool the guard allows, give it a new name when its own is taken, force it only with the typed name when another
 * system used it last, and see the exact command. The pool is imported read-only and nothing is mounted; its card
 * then offers to import it for use or to export it again.
 */
export function ImportDialog({
  app,
  disks,
  onClose,
}: {
  app: PithyxApp;
  disks: StorageDisk[];
  onClose: () => void;
}) {
  const { t } = useTranslation('storage');
  const [guid, setGuid] = useState<string | null>(null);
  const [newName, setNewName] = useState('');
  const [force, setForce] = useState(false);
  const [confirmName, setConfirmName] = useState('');
  const scan = useMutation({
    mutationFn: () => internalCall(app, 'internal.admin.storage.importScan', {}),
    onSuccess: () => {
      setGuid(null);
    },
  });
  const chosen = scan.data?.pools.find((pool) => pool.poolGuid === guid);
  const plan = useMutation({
    mutationFn: () =>
      internalCall(app, 'internal.admin.storage.plan', {
        action: 'pool.import.readonly',
        pool: chosen?.name ?? '',
        poolGuid: guid,
        ...(newName.trim() === '' ? {} : { newName: newName.trim() }),
        ...(force ? { force: true, confirmName } : {}),
      }),
  });
  const { run, job } = useRunPlan(app);
  const ready =
    chosen !== undefined &&
    (!chosen.nameTaken || newName.trim() !== '') &&
    (!chosen.needsForce || (force && confirmName === chosen.name));

  function reset() {
    plan.reset();
  }

  return (
    <Dialog
      isOpen
      size="large"
      isDismissable={job === null || isFinished(job)}
      onOpenChange={(open) => {
        if (!open) {
          onClose();
        }
      }}
      title={t('import.title')}
      actions={
        job !== null ? (
          <Button variant="primary" isDisabled={!isFinished(job)} onPress={onClose}>
            {t('create.close')}
          </Button>
        ) : (
          <>
            <Button onPress={onClose}>{t('create.close')}</Button>
            {plan.data?.planId ? (
              <RunButton preview={plan.data} label={t('import.run')} run={run} job={job} />
            ) : (
              <Button
                variant="primary"
                isDisabled={!ready || plan.isPending}
                onPress={() => {
                  plan.mutate();
                }}
              >
                {t('create.preview')}
              </Button>
            )}
          </>
        )
      }
    >
      <div className={styles.form}>
        <p>{t('import.explain')}</p>
        <p className={styles.muted}>{t('import.scanHint')}</p>
        <div className={styles.buttons}>
          <Button
            isDisabled={scan.isPending || job !== null}
            onPress={() => {
              plan.reset();
              scan.mutate();
            }}
          >
            {t(scan.data === undefined ? 'import.scan' : 'import.rescan')}
          </Button>
        </div>
        {scan.isPending && (
          <p className={styles.muted} role="status">
            {t('import.scanning')}
          </p>
        )}
        {scan.isError && <ErrorNotice error={describeProblem(scan.error)} />}
        {scan.data?.pools.length === 0 && (
          <p className={styles.muted} role="status">
            {t('import.none')}
          </p>
        )}
        {scan.data !== undefined && scan.data.pools.length > 0 && (
          <fieldset className={styles.diskChoice}>
            <legend className={styles.fieldLabel}>{t('import.found')}</legend>
            {scan.data.pools.map((pool) => (
              <ImportCandidate
                key={pool.poolGuid}
                pool={pool}
                disks={disks}
                selected={pool.poolGuid === guid}
                onSelect={() => {
                  setGuid(pool.poolGuid);
                  setNewName('');
                  setForce(false);
                  setConfirmName('');
                  reset();
                }}
              />
            ))}
          </fieldset>
        )}
        {chosen?.nameTaken && (
          <TextField
            label={t('import.newName')}
            description={t('import.nameTaken', { name: chosen.name })}
            value={newName}
            onChange={(value) => {
              setNewName(value);
              reset();
            }}
            autoCapitalize="none"
            autoComplete="off"
          />
        )}
        {chosen?.needsForce && (
          <div className={styles.warnings}>
            <p>{t('import.otherSystem', { name: chosen.name })}</p>
            <label className={styles.check}>
              <input
                type="checkbox"
                checked={force}
                onChange={(event) => {
                  setForce(event.target.checked);
                  reset();
                }}
              />
              <span>{t('import.force')}</span>
            </label>
            {force && (
              <ConfirmNameField
                pool={chosen.name}
                value={confirmName}
                onChange={(value) => {
                  setConfirmName(value);
                  reset();
                }}
              />
            )}
          </div>
        )}
        {plan.isError && <ErrorNotice error={describeProblem(plan.error)} />}
        {plan.data !== undefined && <PlanPreview preview={plan.data} disks={disks} />}
        <RunError run={run} />
        {job !== null && <JobStatus job={job} />}
        {job?.state === 'succeeded' && <p className={styles.muted}>{t('import.done')}</p>}
      </div>
    </Dialog>
  );
}

/** One pool the scan found: its state, layout, disks and whether it can be imported. */
function ImportCandidate({
  pool,
  disks,
  selected,
  onSelect,
}: {
  pool: ImportablePool;
  disks: StorageDisk[];
  selected: boolean;
  onSelect: () => void;
}) {
  const { t } = useTranslation('storage');
  const id = `import-${pool.poolGuid}`;
  const diskName = (diskId: string) =>
    disks.find(({ disk }) => disk.id === diskId)?.disk.name ?? diskId;
  return (
    <div className={styles.candidate} aria-labelledby={id} role="group">
      <label className={styles.check}>
        <input
          type="radio"
          name="importPool"
          checked={selected}
          disabled={!pool.allowed}
          onChange={onSelect}
        />
        <span id={id} className={styles.poolName}>
          {pool.name}
        </span>
        <Badge tone={stateTone(pool.state)}>{t(`state.${stateKey(pool.state)}`)}</Badge>
        {pool.needsForce && <Badge tone="danger">{t('import.usedElsewhere')}</Badge>}
        {pool.nameTaken && <Badge tone="neutral">{t('import.nameTakenBadge')}</Badge>}
      </label>
      <ul className={styles.leaves} aria-label={t('import.layout', { pool: pool.name })}>
        {pool.vdevs.map((vdev) => (
          <ImportVdevItem key={vdev.name} vdev={vdev} diskName={diskName} />
        ))}
      </ul>
      {pool.disks.length > 0 && (
        <ul className={styles.leaves} aria-label={t('import.disks', { pool: pool.name })}>
          {pool.disks.map((verdict) => (
            <li key={verdict.diskId}>
              <span className={styles.mono}>{diskName(verdict.diskId)}</span>:{' '}
              {guardReason(t, verdict)}
            </li>
          ))}
        </ul>
      )}
      {pool.unknownDevices.length > 0 && (
        <p className={styles.errors}>
          {t('import.unknown', { devices: pool.unknownDevices.join(', ') })}
        </p>
      )}
      {!pool.canImport && <p className={styles.errors}>{t('import.cannot')}</p>}
      {(pool.status !== null || pool.action !== null) && (
        <details className={styles.datasets}>
          <summary>{t('import.zfsSays')}</summary>
          <p lang="en">
            {[pool.status, pool.action, pool.messageId].filter((part) => part !== null).join(' ')}
          </p>
        </details>
      )}
    </div>
  );
}

function ImportVdevItem({
  vdev,
  diskName,
}: {
  vdev: ImportVdev;
  diskName: (diskId: string) => string;
}) {
  const { t } = useTranslation('storage');
  return (
    <li className={styles.leaf}>
      <span className={styles.mono}>{vdev.name}</span>
      {vdev.state !== null && (
        <Badge tone={stateTone(vdev.state)}>{t(`state.${stateKey(vdev.state)}`)}</Badge>
      )}
      {vdev.diskId !== null && <span className={styles.muted}>{diskName(vdev.diskId)}</span>}
      {vdev.children.length > 0 && (
        <ul className={styles.leaves}>
          {vdev.children.map((child) => (
            <ImportVdevItem key={child.name} vdev={child} diskName={diskName} />
          ))}
        </ul>
      )}
    </li>
  );
}
