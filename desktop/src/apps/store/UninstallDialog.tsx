import type { PithyxApp } from '@pithyx/sdk';
import { Button, Dialog } from '@pithyx/ui';
import { useMutation } from '@tanstack/react-query';
import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { storeCall } from '../sdk/store';
import { describeProblem } from '../storage/shared';
import { JobProgress, useAppJob } from './jobs';
import styles from './Store.module.css';

/** Removes an app as a job, keeping its data on the app pool for a later install or deleting it. */
export function UninstallDialog({
  app,
  appId,
  name,
  onClose,
}: {
  app: PithyxApp;
  appId: string;
  name: string;
  onClose: () => void;
}) {
  const { t } = useTranslation('store');
  const ids = useId();
  const [data, setData] = useState<'keep' | 'delete'>('keep');
  const { job, start } = useAppJob(app);
  const remove = useMutation({
    mutationFn: () => storeCall(app, 'internal.admin.apps.uninstall', { appId, data }),
    onSuccess: start,
  });
  const close = onClose;

  return (
    <Dialog
      isOpen
      role="alertdialog"
      tone="danger"
      onOpenChange={(open) => {
        if (!open) {
          close();
        }
      }}
      title={t('uninstall.title', { name })}
      actions={
        <>
          <Button onPress={close}>
            {job === null ? t('uninstall.cancel') : t('uninstall.close')}
          </Button>
          {job === null && (
            <Button
              variant="danger"
              isDisabled={remove.isPending}
              onPress={() => {
                remove.mutate();
              }}
            >
              {t('uninstall.confirm')}
            </Button>
          )}
        </>
      }
    >
      <div className={styles.form}>
        {job === null && (
          <fieldset className={styles.choices}>
            <legend className={styles.fieldLabel}>{t('uninstall.data')}</legend>
            {(['keep', 'delete'] as const).map((choice) => (
              <div key={choice}>
                <label className={styles.check}>
                  <input
                    type="radio"
                    name={`${ids}-data`}
                    checked={data === choice}
                    aria-describedby={`${ids}-${choice}`}
                    onChange={() => {
                      setData(choice);
                    }}
                  />
                  <span>{t(`uninstall.${choice}`)}</span>
                </label>
                <p id={`${ids}-${choice}`} className={styles.choiceHint}>
                  {t(`uninstall.${choice}Hint`)}
                </p>
              </div>
            ))}
          </fieldset>
        )}
        <p className={styles.muted}>{t('uninstall.mapped')}</p>
        {remove.isError && <ErrorNotice error={describeProblem(remove.error)} />}
        {job !== null && (
          <JobProgress
            job={job}
            running={t('uninstall.running', { name })}
            succeeded={t('uninstall.done', { name })}
            stopped={t('uninstall.stopped')}
          />
        )}
      </div>
    </Dialog>
  );
}
