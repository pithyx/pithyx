import { unwrap } from '@pithyx/api-client';
import { useMutation } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { api } from '../api/client';
import { describeError } from '../errors/describe';
import { ErrorNotice } from '../errors/ErrorNotice';
import { Button, Dialog } from '@pithyx/ui';

export type PowerAction = 'restart' | 'shutdown';

/** Asks before the box restarts or shuts down, for users with that right, and says that it is on its way. */
export function PowerDialog({ action, onClose }: { action: PowerAction; onClose: () => void }) {
  const { t } = useTranslation();
  const power = useMutation({
    mutationFn: async () => {
      unwrap(await api.POST('/api/system/power', { body: { action } }));
    },
  });
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
