import type { PithyxApp } from '@pithyx/sdk';
import { useMutation } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { formatBytes } from '../../desktop/format';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { Badge, Button, cx, Dialog, TextField } from '@pithyx/ui';
import { internalCall, type StorageDisk } from '../sdk/internal';
import {
  layoutOptions,
  recommendedLayout,
  wastedBytes,
  type Layout,
  type LayoutOption,
} from './layouts';
import { isFinished, JobStatus, PlanPreview, RunButton, RunError, useRunPlan } from './PlanRun';
import styles from './Storage.module.css';
import { describeProblem, guardReason } from './shared';
import { PoolKeyCard } from './PoolEncryption';
import { useFormatLocale } from '../../i18n';

type Step = 'disks' | 'layout' | 'encryption' | 'key' | 'review';

/**
 * Creating a pool: choose the disks, compare every layout that works with them, answer the encryption question (it
 * can only be decided now), store the new pool's key and choose how it unlocks (D384), name the pool, see the exact
 * commands, and run them. Disks that are not empty need their serial typed; the daemon checks every disk again before
 * each command.
 */
export function PoolWizard({
  app,
  disks,
  onClose,
}: {
  app: PithyxApp;
  disks: StorageDisk[];
  onClose: () => void;
}) {
  const { t } = useTranslation('storage');
  const locale = useFormatLocale();
  const [step, setStep] = useState<Step>('disks');
  const [selected, setSelected] = useState<readonly string[]>([]);
  const [confirmations, setConfirmations] = useState<Record<string, string>>({});
  const [layout, setLayout] = useState<Layout | null>(null);
  const [encryption, setEncryption] = useState<'none' | 'key' | null>(null);
  const [unlock, setUnlock] = useState<'vault' | 'manual'>('vault');
  const [keyStored, setKeyStored] = useState(false);
  const [name, setName] = useState('');
  const steps: Step[] =
    encryption === 'key'
      ? ['disks', 'layout', 'encryption', 'key', 'review']
      : ['disks', 'layout', 'encryption', 'review'];
  const newKey = useMutation({
    mutationFn: () => internalCall(app, 'internal.admin.storage.newKey', {}),
  });
  const poolKey = encryption === 'key' ? newKey.data : undefined;
  const usable = disks.filter(({ guard }) => guard.decision !== 'refused');
  const refused = disks.filter(({ guard }) => guard.decision === 'refused');
  const chosen = selected
    .map((id) => disks.find(({ disk }) => disk.id === id))
    .filter((disk) => disk !== undefined);
  const sizes = chosen.map(({ disk }) => disk.sizeBytes);
  const options = layoutOptions(sizes);
  const recommended = recommendedLayout(sizes.length);
  const effectiveLayout =
    layout !== null && options.some((option) => option.layout === layout) ? layout : recommended;
  const plan = useMutation({
    mutationFn: () =>
      internalCall(app, 'internal.admin.storage.plan', {
        action: 'pool.create',
        pool: name.trim(),
        layout: effectiveLayout,
        diskIds: [...selected],
        ...(poolKey === undefined
          ? { encryption: 'none' }
          : { encryption: 'key', encryptionKey: poolKey.key, keepKeyInVault: unlock === 'vault' }),
        confirmations: Object.fromEntries(
          Object.entries(confirmations).filter(([id]) => selected.includes(id)),
        ),
      }),
  });
  const { run, job } = useRunPlan(app);

  function toggle(id: string, on: boolean) {
    setSelected(on ? [...selected, id] : selected.filter((other) => other !== id));
    plan.reset();
  }

  const index = steps.indexOf(step);
  const canContinue =
    step === 'disks'
      ? selected.length > 0
      : step === 'layout'
        ? true
        : step === 'encryption'
          ? encryption !== null
          : step === 'key'
            ? poolKey !== undefined && keyStored
            : false;

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
      title={t('create.title')}
      actions={
        job !== null ? (
          <Button variant="primary" isDisabled={!isFinished(job)} onPress={onClose}>
            {t('create.close')}
          </Button>
        ) : (
          <>
            {index > 0 ? (
              <Button
                onPress={() => {
                  setStep(steps[index - 1] ?? 'disks');
                  plan.reset();
                }}
              >
                {t('wizard.back')}
              </Button>
            ) : (
              <Button onPress={onClose}>{t('create.close')}</Button>
            )}
            {step === 'review' ? (
              plan.data?.planId ? (
                <RunButton preview={plan.data} label={t('wizard.create')} run={run} job={job} />
              ) : (
                <Button
                  variant="primary"
                  isDisabled={name.trim() === '' || plan.isPending}
                  onPress={() => {
                    plan.mutate();
                  }}
                >
                  {t('create.preview')}
                </Button>
              )
            ) : (
              <Button
                variant="primary"
                isDisabled={!canContinue}
                onPress={() => {
                  const next = steps[index + 1] ?? 'review';
                  // One key per wizard: going back and forth never replaces a key the user may have stored already.
                  if (next === 'key' && newKey.data === undefined && !newKey.isPending) {
                    newKey.mutate();
                  }
                  // The card asks for the two groups again each time the step opens.
                  if (next === 'key') {
                    setKeyStored(false);
                  }
                  setStep(next);
                }}
              >
                {t('wizard.next')}
              </Button>
            )}
          </>
        )
      }
    >
      <div className={styles.form}>
        <p className={styles.muted}>
          {t('wizard.progress', {
            step: index + 1,
            count: steps.length,
            title: t(`wizard.steps.${step}`),
          })}
        </p>

        {step === 'disks' && (
          <fieldset className={styles.diskChoice}>
            <legend className={styles.fieldLabel}>{t('create.disks')}</legend>
            {usable.length === 0 && <p className={styles.muted}>{t('create.noDisks')}</p>}
            {usable.map(({ disk, guard }) => (
              <div key={disk.id} className={styles.diskOption}>
                {/* A native checkbox: React Aria's would move into the chunk every app shares with the desktop. */}
                <label className={styles.check}>
                  <input
                    type="checkbox"
                    checked={selected.includes(disk.id)}
                    onChange={(event) => {
                      toggle(disk.id, event.target.checked);
                    }}
                  />
                  <span className={styles.mono}>{disk.name}</span>
                  <span className={styles.muted}>
                    {disk.serial ?? ''} · {formatBytes(disk.sizeBytes, locale)}
                  </span>
                </label>
                {guard.decision === 'needsSerialConfirmation' && selected.includes(disk.id) && (
                  <TextField
                    label={t('create.confirm', {
                      disk: disk.name,
                      serial: disk.serial ?? '',
                    })}
                    description={guardReason(t, guard)}
                    value={confirmations[disk.id] ?? ''}
                    onChange={(value) => {
                      setConfirmations({ ...confirmations, [disk.id]: value });
                      plan.reset();
                    }}
                    autoCapitalize="none"
                    autoComplete="off"
                  />
                )}
              </div>
            ))}
            {refused.length > 0 && (
              <details className={styles.refused}>
                <summary>{t('create.refused', { count: refused.length })}</summary>
                <ul>
                  {refused.map(({ disk, guard }) => (
                    <li key={disk.id}>
                      <span className={styles.mono}>{disk.name}</span>: {guardReason(t, guard)}
                    </li>
                  ))}
                </ul>
              </details>
            )}
          </fieldset>
        )}

        {step === 'layout' && (
          <>
            <p className={styles.recommendation}>
              {t(`wizard.recommend.${recommended}`, { count: sizes.length })}
            </p>
            {wastedBytes(sizes) > 0 && (
              <p className={styles.notice}>
                {t('wizard.mixedSizes', {
                  wasted: formatBytes(wastedBytes(sizes), locale),
                })}
              </p>
            )}
            <div className={styles.layouts} role="radiogroup" aria-label={t('create.layout')}>
              {options.map((option) => (
                <LayoutCard
                  key={option.layout}
                  option={option}
                  recommended={option.layout === recommended}
                  selected={option.layout === effectiveLayout}
                  onSelect={() => {
                    setLayout(option.layout);
                  }}
                />
              ))}
            </div>
          </>
        )}

        {step === 'encryption' && (
          <fieldset className={styles.diskChoice}>
            <legend className={styles.fieldLabel}>{t('wizard.encryption.question')}</legend>
            <p className={styles.muted}>{t('wizard.encryption.explain')}</p>
            <label className={styles.check}>
              <input
                type="radio"
                name="encryption"
                checked={encryption === 'none'}
                onChange={() => {
                  setEncryption('none');
                  plan.reset();
                }}
              />
              <span>{t('wizard.encryption.none')}</span>
            </label>
            <p className={cx(styles.muted, styles.indent)}>{t('wizard.encryption.noneHint')}</p>
            <label className={styles.check}>
              <input
                type="radio"
                name="encryption"
                checked={encryption === 'key'}
                onChange={() => {
                  setEncryption('key');
                  plan.reset();
                }}
              />
              <span>{t('wizard.encryption.key')}</span>
            </label>
            <p className={cx(styles.muted, styles.indent)}>{t('wizard.encryption.keyHint')}</p>
            {encryption === 'key' && (
              <fieldset className={cx(styles.diskChoice, styles.indent)}>
                <legend className={styles.fieldLabel}>{t('wizard.encryption.unlock')}</legend>
                <label className={styles.check}>
                  <input
                    type="radio"
                    name="unlock"
                    checked={unlock === 'vault'}
                    onChange={() => {
                      setUnlock('vault');
                      plan.reset();
                    }}
                  />
                  <span>{t('wizard.encryption.vault')}</span>
                </label>
                <p className={cx(styles.muted, styles.indent)}>{t('vault.hint')}</p>
                <label className={styles.check}>
                  <input
                    type="radio"
                    name="unlock"
                    checked={unlock === 'manual'}
                    onChange={() => {
                      setUnlock('manual');
                      plan.reset();
                    }}
                  />
                  <span>{t('wizard.encryption.manual')}</span>
                </label>
                <p className={cx(styles.muted, styles.indent)}>
                  {t('wizard.encryption.manualHint')}
                </p>
              </fieldset>
            )}
          </fieldset>
        )}

        {step === 'key' && (
          <>
            {/* Named here already, so the downloaded or printed key says which pool it opens. */}
            <TextField
              label={t('create.name')}
              description={t('create.nameHint')}
              value={name}
              onChange={(value) => {
                setName(value);
                plan.reset();
              }}
              autoCapitalize="none"
              autoComplete="off"
            />
            {newKey.isError && <ErrorNotice error={describeProblem(newKey.error)} />}
            {poolKey !== undefined && (
              <PoolKeyCard poolKey={poolKey} pool={name.trim()} onStoredChange={setKeyStored} />
            )}
          </>
        )}

        {step === 'review' && (
          <>
            <TextField
              label={t('create.name')}
              description={t('create.nameHint')}
              value={name}
              onChange={(value) => {
                setName(value);
                plan.reset();
              }}
              autoCapitalize="none"
              autoComplete="off"
              isReadOnly={job !== null}
            />
            <p>
              {t('wizard.summary', {
                layout: t(`wizard.layouts.${effectiveLayout}.title`),
                count: selected.length,
                usable: formatBytes(
                  options.find((option) => option.layout === effectiveLayout)?.usableBytes ?? 0,
                  locale,
                ),
              })}
            </p>
            <p className={styles.muted}>
              {t(
                poolKey === undefined
                  ? 'wizard.encryption.summaryNone'
                  : unlock === 'vault'
                    ? 'wizard.encryption.summaryVault'
                    : 'wizard.encryption.summaryManual',
              )}
            </p>
            {plan.isError && <ErrorNotice error={describeProblem(plan.error)} />}
            {plan.data !== undefined && <PlanPreview preview={plan.data} disks={disks} />}
            <RunError run={run} />
            {job !== null && <JobStatus job={job} />}
          </>
        )}
      </div>
    </Dialog>
  );
}

function Meter({ label, value }: { label: string; value: number }) {
  return (
    <span className={styles.meter}>
      <span>{label}</span>
      <span className={styles.dots} aria-hidden>
        {[1, 2, 3, 4, 5].map((dot) => (
          <span key={dot} className={cx(styles.dot, dot <= value && styles.dotOn)} />
        ))}
      </span>
    </span>
  );
}

function LayoutCard({
  option,
  recommended,
  selected,
  onSelect,
}: {
  option: LayoutOption;
  recommended: boolean;
  selected: boolean;
  onSelect: () => void;
}) {
  const { t } = useTranslation('storage');
  const locale = useFormatLocale();
  const key = `wizard.layouts.${option.layout}` as const;
  const tolerance =
    option.tolerance === 0
      ? t('wizard.tolerance.none')
      : option.toleranceMax > option.tolerance
        ? t('wizard.tolerance.pairs', { max: option.toleranceMax })
        : t('wizard.tolerance.any', { count: option.tolerance });
  const speeds = ['low', 'low', 'medium', 'good', 'high', 'veryHigh'] as const;
  return (
    <button
      type="button"
      role="radio"
      aria-checked={selected}
      className={cx(
        styles.layoutCard,
        selected && styles.layoutSelected,
        option.tolerance === 0 && styles.layoutRisky,
      )}
      onClick={onSelect}
    >
      <span className={styles.layoutHead}>
        <span className={styles.layoutTitle}>{t(`${key}.title`)}</span>
        {recommended && <Badge tone="accent">{t('wizard.recommended')}</Badge>}
      </span>
      <span className={styles.drawing} aria-hidden>
        {option.groups.map((group, groupIndex) => (
          <span key={groupIndex} className={styles.group}>
            {group.map((role, index) => (
              <span key={index} className={styles.diskBox} data-role={role} />
            ))}
          </span>
        ))}
      </span>
      <span className={styles.legend}>
        {t('wizard.drawing', {
          data: option.groups.flat().filter((role) => role === 'data').length,
          protection: option.groups.flat().filter((role) => role !== 'data').length,
        })}
      </span>
      <span className={styles.usable}>
        {t('wizard.usable', { size: formatBytes(option.usableBytes, locale) })}
      </span>
      <span className={option.tolerance === 0 ? styles.errors : undefined}>{tolerance}</span>
      <Meter
        label={t('wizard.read', {
          speed: t(`wizard.speed.${speeds[option.read] ?? 'low'}`),
        })}
        value={option.read}
      />
      <Meter
        label={t('wizard.write', {
          speed: t(`wizard.speed.${speeds[option.write] ?? 'low'}`),
        })}
        value={option.write}
      />
      <span className={styles.goodFor}>{t(`${key}.goodFor`)}</span>
      <span className={styles.fixed}>{t(`${key}.fixed`)}</span>
    </button>
  );
}
