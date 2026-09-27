import type { PithyxApp } from '@pithyx/sdk';
import { useMutation } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { formatBytes } from '../../desktop/format';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { Button } from '../../ui/Button';
import { Dialog } from '../../ui/Dialog';
import { TextField } from '../../ui/TextField';
import { internalCall, type StorageDisk } from '../sdk/internal';
import styles from './Settings.module.css';
import { describeProblem, guardReason } from './StoragePage';

export const layouts = ['single', 'mirror', 'raidz1', 'raidz2', 'raidz3', 'stripe'] as const;

/**
 * Shows the exact commands a new pool would run. Disks that are not empty need their serial typed; the daemon probes
 * the chosen disks again and asks the disk guard. Nothing runs from here yet.
 */
export function CreatePoolDialog({
  app,
  disks,
  onClose,
}: {
  app: PithyxApp;
  disks: StorageDisk[];
  onClose: () => void;
}) {
  const { t, i18n } = useTranslation('settings');
  const [name, setName] = useState('');
  const [layout, setLayout] = useState<(typeof layouts)[number]>('mirror');
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set());
  const [confirmations, setConfirmations] = useState<Record<string, string>>({});
  const preview = useMutation({
    mutationFn: () =>
      internalCall(app, 'internal.admin.storage.preview', {
        name: name.trim(),
        layout,
        diskIds: [...selected],
        confirmations: Object.fromEntries(
          Object.entries(confirmations).filter(([id]) => selected.has(id)),
        ),
      }),
  });
  const usable = disks.filter(({ guard }) => guard.decision !== 'refused');
  const refused = disks.filter(({ guard }) => guard.decision === 'refused');

  function toggle(id: string, on: boolean) {
    const next = new Set(selected);
    if (on) {
      next.add(id);
    } else {
      next.delete(id);
    }
    setSelected(next);
    preview.reset();
  }

  return (
    <Dialog
      isOpen
      isDismissable
      onOpenChange={(open) => {
        if (!open) {
          onClose();
        }
      }}
      title={t('storage.create.title')}
      actions={
        <>
          <Button onPress={onClose}>{t('storage.create.close')}</Button>
          <Button
            variant="primary"
            isDisabled={name.trim() === '' || selected.size === 0 || preview.isPending}
            onPress={() => {
              preview.mutate();
            }}
          >
            {t('storage.create.preview')}
          </Button>
        </>
      }
    >
      <div className={styles.form}>
        <TextField
          label={t('storage.create.name')}
          description={t('storage.create.nameHint')}
          value={name}
          onChange={(value) => {
            setName(value);
            preview.reset();
          }}
          autoCapitalize="none"
          autoComplete="off"
        />
        <label className={styles.selectField}>
          <span className={styles.fieldLabel}>{t('storage.create.layout')}</span>
          <select
            className={styles.select}
            value={layout}
            onChange={(event) => {
              setLayout(event.target.value as (typeof layouts)[number]);
              preview.reset();
            }}
          >
            {layouts.map((option) => (
              <option key={option} value={option}>
                {t(`storage.layouts.${option}`)}
              </option>
            ))}
          </select>
        </label>
        <fieldset className={styles.diskChoice}>
          <legend className={styles.fieldLabel}>{t('storage.create.disks')}</legend>
          {usable.length === 0 && <p className={styles.muted}>{t('storage.create.noDisks')}</p>}
          {usable.map(({ disk, guard }) => (
            <div key={disk.id} className={styles.diskOption}>
              {/* A native checkbox: React Aria's would move into the chunk every app shares with the desktop. */}
              <label className={styles.check}>
                <input
                  type="checkbox"
                  checked={selected.has(disk.id)}
                  onChange={(event) => {
                    toggle(disk.id, event.target.checked);
                  }}
                />
                <span className={styles.mono}>{disk.name}</span>
                <span className={styles.muted}>
                  {disk.serial ?? ''} · {formatBytes(disk.sizeBytes, i18n.language)}
                </span>
              </label>
              {guard.decision === 'needsSerialConfirmation' && selected.has(disk.id) && (
                <TextField
                  label={t('storage.create.confirm', {
                    disk: disk.name,
                    serial: disk.serial ?? '',
                  })}
                  description={guardReason(t, guard)}
                  value={confirmations[disk.id] ?? ''}
                  onChange={(value) => {
                    setConfirmations({ ...confirmations, [disk.id]: value });
                    preview.reset();
                  }}
                  autoCapitalize="none"
                  autoComplete="off"
                  inputClassName={styles.mono}
                />
              )}
            </div>
          ))}
          {refused.length > 0 && (
            <details className={styles.refused}>
              <summary>{t('storage.create.refused', { count: refused.length })}</summary>
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

        {preview.isError && <ErrorNotice error={describeProblem(preview.error)} />}
        {preview.data !== undefined && (
          <div className={styles.preview} role="status">
            {preview.data.commands === null ? (
              <>
                <p>{t('storage.create.blocked')}</p>
                <ul>
                  {preview.data.disks
                    .filter((verdict) => verdict.decision !== 'allowed')
                    .map((verdict) => (
                      <li key={verdict.diskId}>
                        <span className={styles.mono}>
                          {disks.find(({ disk }) => disk.id === verdict.diskId)?.disk.name ??
                            verdict.diskId}
                        </span>
                        : {guardReason(t, verdict)}
                      </li>
                    ))}
                </ul>
              </>
            ) : (
              <>
                <p>{t('storage.create.commands')}</p>
                <ol className={styles.commands}>
                  {preview.data.commands.map((command, index) => (
                    <li key={index}>
                      <code>{[command.program, ...command.arguments].join(' ')}</code>
                    </li>
                  ))}
                </ol>
                <p className={styles.muted}>{t('storage.create.notYet')}</p>
              </>
            )}
          </div>
        )}
      </div>
    </Dialog>
  );
}
