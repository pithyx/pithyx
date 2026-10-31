import { useTranslation } from 'react-i18next';
import { Button, Dialog } from '@pithyx/ui';
import { TrustSteps } from './TrustSteps';

/** The trust steps from the hint and the user menu. */
export function TrustDialog({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation('security');
  return (
    <Dialog
      isOpen
      onOpenChange={(open) => {
        if (!open) {
          onClose();
        }
      }}
      size="medium"
      title={t('trust.title')}
      actions={<Button onPress={onClose}>{t('trust.close')}</Button>}
    >
      <TrustSteps />
    </Dialog>
  );
}
