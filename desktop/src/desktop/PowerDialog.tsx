import { unwrap } from '@pithyx/api-client';
import { useMutation } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { api } from '../api/client';
import { runBeforeUnload } from '../apps/lifecycle';
import { describeError } from '../errors/describe';
import { ErrorNotice } from '../errors/ErrorNotice';
import { useRealtime } from '../realtime/connection';
import { Button, Dialog } from '@pithyx/ui';

export type PowerAction = 'restart' | 'shutdown';

/** Asks before the box restarts or shuts down, for users with that right, and says that it is on its way. */
export function PowerDialog({ action, onClose }: { action: PowerAction; onClose: () => void }) {
  const { t } = useTranslation();
  const power = useMutation({
    mutationFn: async () => {
      // Apps get a last chance to save; they cannot stop it.
      await runBeforeUnload(action);
      unwrap(await api.POST('/api/system/power', { body: { action } }));
    },
  });
  // The restart is over once the connection that dropped with it is back.
  const status = useRealtime((state) => state.status);
  const [dropped, setDropped] = useState(false);
  if (power.isSuccess && status !== 'open' && !dropped) {
    setDropped(true);
  }
  const back = action === 'restart' && dropped && status === 'open';
  useEffect(() => {
    if (back) {
      onClose();
    }
  }, [back, onClose]);
  return (
    <Dialog
      isOpen
      isDismissable={!power.isSuccess}
      tone={action === 'shutdown' ? 'danger' : 'default'}
      onOpenChange={(open) => {
        if (!open) {
          onClose();
        }
      }}
      title={t(`power.${action}.title`)}
      actions={
        power.isSuccess ? null : (
          <>
            <Button onPress={onClose}>{t('power.cancel')}</Button>
            <Button
              variant={action === 'shutdown' ? 'danger' : 'primary'}
              isDisabled={power.isPending}
              onPress={() => {
                power.mutate();
              }}
            >
              {t(`power.${action}.confirm`)}
            </Button>
          </>
        )
      }
    >
      <p role="status">
        {power.isSuccess ? t(`power.${action}.done`) : t(`power.${action}.intro`)}
      </p>
      {power.isError && <ErrorNotice error={describeError(power.error)} />}
    </Dialog>
  );
}
