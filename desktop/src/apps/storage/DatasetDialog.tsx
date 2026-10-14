import type { PithyxApp } from '@pithyx/sdk';
import { useMutation } from '@tanstack/react-query';
import { useId, useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { formatBytes } from '../../desktop/format';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { Button, Dialog, TextField } from '@pithyx/ui';
import {
  internalCall,
  type Dataset,
  type Snapshot,
  type StoragePlanRequest,
} from '../sdk/internal';
import {
  COMPRESSIONS,
  compressionOnly,
  datasetRole,
  parseSize,
  RECORD_SIZES,
  SIZE_UNITS,
  sizeText,
  type SizeUnit,
} from './datasets';
import { isFinished, JobStatus, PlanPreview, RunButton, RunError, useRunPlan } from './PlanRun';
import { describeProblem } from './shared';
import styles from './Storage.module.css';
import { useFormatLocale } from '../../i18n';

export type DatasetAction =
  | { kind: 'rename'; dataset: Dataset }
  | { kind: 'limits'; dataset: Dataset }
  | { kind: 'destroy'; dataset: Dataset }
  | { kind: 'snapshotDestroy'; dataset: Dataset; snapshot: Snapshot }
  | { kind: 'rollback'; dataset: Dataset; snapshot: Snapshot; newer: number };

/** A snapshot is deleted without typing anything: the preview names it and the run button confirms. */
function initialRequest(action: DatasetAction): StoragePlanRequest | null {
  return action.kind === 'snapshotDestroy'
    ? {
        action: 'snapshot.destroy',
        pool: action.dataset.pool,
        dataset: action.dataset.name,
        name: action.snapshot.name,
      }
    : null;
}

/**
 * One dataset, volume or snapshot action through a command plan: fill in the fields, see the exact commands (and for
 * a delete or rollback everything that goes), run them. The daemon decides what is allowed and says why it refuses.
 */
export function DatasetDialog({
  app,
  action,
  onClose,
}: {
  app: PithyxApp;
  action: DatasetAction;
  onClose: () => void;
}) {
  const { t } = useTranslation('storage');
  const [request, setRequest] = useState(() => initialRequest(action));
  const plan = useMutation({
    mutationFn: (body: StoragePlanRequest) =>
      internalCall(app, 'internal.admin.storage.plan', body),
  });
  const { run, job } = useRunPlan(app);
  const destroy =
    action.kind === 'destroy' || action.kind === 'snapshotDestroy' || action.kind === 'rollback';
  const target = action.dataset.name;
  const change = (next: StoragePlanRequest | null) => {
    setRequest(next);
    plan.reset();
  };

  return (
    <Dialog
      isOpen
      isDismissable={job === null || isFinished(job)}
      tone={destroy ? 'danger' : 'default'}
      onOpenChange={(open) => {
        if (!open) {
          onClose();
        }
      }}
      title={t(`datasets.${action.kind}.title`, { dataset: target, pool: target })}
      actions={
        job !== null ? (
          <Button variant="primary" isDisabled={!isFinished(job)} onPress={onClose}>
            {t('create.close')}
          </Button>
        ) : (
          <>
            <Button onPress={onClose}>{t('create.close')}</Button>
            {plan.data?.planId ? (
              <RunButton
                preview={plan.data}
                label={t(`datasets.${action.kind}.run`)}
                run={run}
                job={job}
                danger={destroy}
              />
            ) : (
              <Button
                variant="primary"
                isDisabled={request === null || plan.isPending}
                onPress={() => {
                  if (request !== null) {
                    plan.mutate(request);
                  }
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
        {action.kind === 'rename' && <RenameFields dataset={action.dataset} onChange={change} />}
        {action.kind === 'limits' && <LimitFields dataset={action.dataset} onChange={change} />}
        {action.kind === 'destroy' && <DestroyFields dataset={action.dataset} onChange={change} />}
        {action.kind === 'snapshotDestroy' && (
          <p>
            {t('datasets.snapshotDestroy.explain', {
              name: action.snapshot.name,
              dataset: action.dataset.name,
            })}
          </p>
        )}
        {action.kind === 'rollback' && (
          <RollbackFields
            dataset={action.dataset}
            snapshot={action.snapshot}
            newer={action.newer}
            date={app.format.dateTime(action.snapshot.createdAt)}
            onChange={change}
          />
        )}
        {plan.isError && <ErrorNotice error={describeProblem(plan.error)} />}
        {plan.data !== undefined && (
          <PlanPreview
            preview={plan.data}
            disks={[]}
            datasetsLabel={
              action.kind === 'destroy' ||
              action.kind === 'rename' ||
              action.kind === 'snapshotDestroy' ||
              action.kind === 'rollback'
                ? t(`datasets.${action.kind}.listed`)
                : undefined
            }
          />
        )}
        <RunError run={run} />
        {job !== null && <JobStatus job={job} />}
      </div>
    </Dialog>
  );
}

type OnChange = (request: StoragePlanRequest | null) => void;

function RenameFields({ dataset, onChange }: { dataset: Dataset; onChange: OnChange }) {
  const { t } = useTranslation('storage');
  const [name, setName] = useState('');
  const parent = dataset.name.slice(0, dataset.name.lastIndexOf('/'));
  const current = dataset.name.slice(parent.length + 1);
  return (
    <>
      <p>{t('datasets.rename.explain', { parent })}</p>
      <TextField
        label={t('datasets.rename.newName')}
        description={t('datasets.rename.hint', { name: current })}
        value={name}
        onChange={(value) => {
          setName(value);
          const trimmed = value.trim();
          onChange(
            trimmed === '' || trimmed === current
              ? null
              : {
                  action: 'dataset.rename',
                  pool: dataset.pool,
                  dataset: dataset.name,
                  newName: trimmed,
                },
          );
        }}
        autoCapitalize="none"
        autoComplete="off"
        spellCheck="false"
        inputClassName={styles.mono}
      />
    </>
  );
}

interface Limits {
  quota: { text: string; unit: SizeUnit };
  reservation: { text: string; unit: SizeUnit };
  compression: string;
  recordSize: number | null;
}

function LimitFields({ dataset, onChange }: { dataset: Dataset; onChange: OnChange }) {
  const { t } = useTranslation('storage');
  const locale = useFormatLocale();
  const role = datasetRole(dataset.pool, dataset.name);
  const onlyCompression = compressionOnly(role, dataset.type);
  const [initial] = useState<Limits>(() => ({
    quota: sizeText(dataset.quotaBytes, locale),
    reservation: sizeText(dataset.reservationBytes, locale),
    compression: dataset.compression ?? 'lz4',
    recordSize: dataset.recordSizeBytes,
  }));
  const [limits, setLimits] = useState<Limits>(initial);
  const quota = parseSize(limits.quota.text, limits.quota.unit);
  const reservation = parseSize(limits.reservation.text, limits.reservation.unit);

  const update = (next: Limits) => {
    setLimits(next);
    const nextQuota = parseSize(next.quota.text, next.quota.unit);
    const nextReservation = parseSize(next.reservation.text, next.reservation.unit);
    // Only what the user changed is sent: a value shown rounded must not come back as a new one.
    const quotaChanged =
      next.quota.text !== initial.quota.text || next.quota.unit !== initial.quota.unit;
    const reservationChanged =
      next.reservation.text !== initial.reservation.text ||
      next.reservation.unit !== initial.reservation.unit;
    const request: StoragePlanRequest = {
      action: 'dataset.set',
      pool: dataset.pool,
      dataset: dataset.name,
      ...(quotaChanged && nextQuota !== undefined ? { quotaBytes: nextQuota ?? 0 } : {}),
      ...(reservationChanged && nextReservation !== undefined
        ? { reservationBytes: nextReservation ?? 0 }
        : {}),
      ...(next.compression !== initial.compression ? { compression: next.compression } : {}),
      ...(next.recordSize !== initial.recordSize && next.recordSize !== null
        ? { recordSizeBytes: next.recordSize }
        : {}),
    };
    const invalid = nextQuota === undefined || nextReservation === undefined;
    onChange(invalid || Object.keys(request).length === 3 ? null : request);
  };

  return (
    <>
      <p>{onlyCompression ? t('datasets.limits.compressionOnly') : t('datasets.limits.explain')}</p>
      {!onlyCompression && (
        <>
          <SizeField
            label={t('datasets.limits.quota')}
            description={t('datasets.limits.quotaHint', {
              used: formatBytes(dataset.usedBytes, locale),
            })}
            value={limits.quota}
            invalid={quota === undefined}
            onChange={(value) => {
              update({ ...limits, quota: value });
            }}
          />
          <SizeField
            label={t('datasets.limits.reservation')}
            description={t('datasets.limits.reservationHint')}
            value={limits.reservation}
            invalid={reservation === undefined}
            onChange={(value) => {
              update({ ...limits, reservation: value });
            }}
          />
        </>
      )}
      <Select
        label={t('datasets.limits.compression')}
        description={t('datasets.limits.compressionHint')}
        value={limits.compression}
        onChange={(value) => {
          update({ ...limits, compression: value });
        }}
      >
        {!COMPRESSIONS.some((known) => known === initial.compression) && (
          <option value={initial.compression}>{initial.compression}</option>
        )}
        {COMPRESSIONS.map((compression) => (
          <option key={compression} value={compression}>
            {compression === 'off'
              ? t('datasets.limits.compressionOff')
              : compression === 'lz4'
                ? t('datasets.limits.compressionDefault', { name: compression })
                : compression}
          </option>
        ))}
      </Select>
      {!onlyCompression && dataset.type === 'filesystem' && limits.recordSize !== null && (
        <Select
          label={t('datasets.limits.recordSize')}
          description={t('datasets.limits.recordSizeHint')}
          value={String(limits.recordSize)}
          onChange={(value) => {
            update({ ...limits, recordSize: Number(value) });
          }}
        >
          {RECORD_SIZES.map((size) => (
            <option key={size} value={size}>
              {size >= 1024 * 1024
                ? t('datasets.limits.mib', { value: size / (1024 * 1024) })
                : t('datasets.limits.kib', { value: size / 1024 })}
            </option>
          ))}
        </Select>
      )}
    </>
  );
}

function DestroyFields({ dataset, onChange }: { dataset: Dataset; onChange: OnChange }) {
  const { t } = useTranslation('storage');
  const [typed, setTyped] = useState('');
  return (
    <>
      <p>
        {dataset.type === 'volume'
          ? t('datasets.destroy.explainVolume', { dataset: dataset.name })
          : t('datasets.destroy.explain', { dataset: dataset.name })}
      </p>
      <TextField
        label={t('datasets.destroy.confirm', { dataset: dataset.name })}
        value={typed}
        onChange={(value) => {
          setTyped(value);
          onChange(
            value === dataset.name
              ? {
                  action: 'dataset.destroy',
                  pool: dataset.pool,
                  dataset: dataset.name,
                  confirmName: value,
                }
              : null,
          );
        }}
        autoCapitalize="none"
        autoComplete="off"
        spellCheck="false"
        inputClassName={styles.mono}
      />
    </>
  );
}

function RollbackFields({
  dataset,
  snapshot,
  newer,
  date,
  onChange,
}: {
  dataset: Dataset;
  snapshot: Snapshot;
  newer: number;
  date: string;
  onChange: OnChange;
}) {
  const { t } = useTranslation('storage');
  const [typed, setTyped] = useState('');
  return (
    <>
      <p>{t('datasets.rollback.explain', { name: snapshot.name, date })}</p>
      {newer > 0 && <p>{t('datasets.rollback.newer', { count: newer })}</p>}
      <TextField
        label={t('datasets.rollback.confirm', { dataset: dataset.name })}
        value={typed}
        onChange={(value) => {
          setTyped(value);
          onChange(
            value === dataset.name
              ? {
                  action: 'snapshot.rollback',
                  pool: dataset.pool,
                  dataset: dataset.name,
                  name: snapshot.name,
                  confirmName: value,
                }
              : null,
          );
        }}
        autoCapitalize="none"
        autoComplete="off"
        spellCheck="false"
        inputClassName={styles.mono}
      />
    </>
  );
}

/** A number with its unit select inside the field's frame; empty text means none. */
function SizeField({
  label,
  description,
  value,
  invalid,
  onChange,
}: {
  label: string;
  description: string;
  value: { text: string; unit: SizeUnit };
  invalid: boolean;
  onChange: (value: { text: string; unit: SizeUnit }) => void;
}) {
  const { t } = useTranslation('storage');
  return (
    <TextField
      label={label}
      description={description}
      value={value.text}
      inputMode="decimal"
      autoComplete="off"
      isInvalid={invalid}
      errorMessage={t('datasets.limits.invalidNumber')}
      onChange={(text) => {
        onChange({ ...value, text });
      }}
      trailing={
        <select
          className={styles.unit}
          aria-label={t('datasets.limits.unit', { field: label })}
          value={value.unit}
          onChange={(event) => {
            onChange({ ...value, unit: event.currentTarget.value as SizeUnit });
          }}
        >
          {Object.keys(SIZE_UNITS).map((unit) => (
            <option key={unit} value={unit}>
              {t(`datasets.units.${unit as SizeUnit}`)}
            </option>
          ))}
        </select>
      }
    />
  );
}

function Select({
  label,
  description,
  value,
  onChange,
  children,
}: {
  label: string;
  description: string;
  value: string;
  onChange: (value: string) => void;
  children: ReactNode;
}) {
  const id = useId();
  return (
    <div className={styles.selectField}>
      <label htmlFor={id} className={styles.fieldLabel}>
        {label}
      </label>
      <select
        id={id}
        className={styles.select}
        aria-describedby={`${id}-description`}
        value={value}
        onChange={(event) => {
          onChange(event.currentTarget.value);
        }}
      >
        {children}
      </select>
      <span id={`${id}-description`} className={styles.muted}>
        {description}
      </span>
    </div>
  );
}
