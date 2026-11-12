import { isProblem } from '@pithyx/sdk';
import { Button, Dialog } from '@pithyx/ui';
import { useState, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';

/** A change the administrator did not confirm; nothing to show for it. */
export class AppsAccessCancelled extends Error {}

/** Whether an error is a declined confirmation, which the dialogs do not show as an error. */
export function isAppsAccessCancelled(error: unknown): boolean {
  return error instanceof AppsAccessCancelled;
}

/**
 * The confirmation before a user or a group gets more of Apps (D409): it holds the data of every app. Files warns
 * before a change in a running app's folder, SMB cannot, and the other administrators are told. `confirmed` runs a
 * change and, when the daemon answers `fs.apps.confirmation_required`, asks and runs it again confirmed.
 */
export function useAppsAccessConfirmation(): {
  ask: () => Promise<boolean>;
  confirmed: <T>(change: (confirm: boolean) => Promise<T>) => Promise<T>;
  dialog: ReactNode;
} {
  const { t } = useTranslation('users');
  const [pending, setPending] = useState<((go: boolean) => void) | null>(null);
  const ask = () =>
    new Promise<boolean>((answer) => {
      setPending(() => answer);
    });
  const answer = (go: boolean) => {
    pending?.(go);
    setPending(null);
  };
  return {
    ask,
    confirmed: async (change) => {
      try {
        return await change(false);
      } catch (error) {
        if (!isProblem(error) || error.code !== 'fs.apps.confirmation_required') {
          throw error;
        }
        if (!(await ask())) {
          throw new AppsAccessCancelled();
        }
        return change(true);
      }
    },
    dialog: (
      <Dialog
        isOpen={pending !== null}
        onOpenChange={(isOpen) => {
          if (!isOpen) {
            answer(false);
          }
        }}
        role="alertdialog"
        tone="danger"
        title={t('appsAccess.title')}
        actions={
          <>
            <Button
              onPress={() => {
                answer(false);
              }}
            >
              {t('appsAccess.cancel')}
            </Button>
            <Button
              variant="danger"
              onPress={() => {
                answer(true);
              }}
            >
              {t('appsAccess.confirm')}
            </Button>
          </>
        }
      >
        <p>{t('appsAccess.body')}</p>
        <p>{t('appsAccess.smb')}</p>
        <p>{t('appsAccess.notice')}</p>
      </Dialog>
    ),
  };
}
