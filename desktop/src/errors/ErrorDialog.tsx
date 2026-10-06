import { IconAlertTriangle } from '../ui/icons';
import { useTranslation } from 'react-i18next';
import { Button, Dialog, UiProvider } from '@pithyx/ui';
import { useFormatLocale } from '../i18n';
import type { DescribedError } from './describe';
import { useErrorMessage, useLogRequestId } from './ErrorNotice';
import { closeError, useErrorStore } from './errorStore';

function ErrorContent({ error }: { error: DescribedError }) {
  const message = useErrorMessage(error);
  useLogRequestId(error);
  return <p>{message}</p>;
}

/** Errors that do not belong to a form: the translated message. */
export function ErrorDialog() {
  const { t } = useTranslation();
  const error = useErrorStore((state) => state.current);
  const locale = useFormatLocale();
  return (
    <UiProvider locale={locale}>
      <Dialog
        isOpen={error !== null}
        onOpenChange={(open) => {
          if (!open) {
            closeError();
          }
        }}
        isDismissable
        role="alertdialog"
        tone="danger"
        icon={<IconAlertTriangle size={22} stroke={1.75} />}
        title={t('error.title')}
        actions={
          <Button variant="primary" slot="close" autoFocus>
            {t('error.close')}
          </Button>
        }
      >
        {error !== null && <ErrorContent error={error} />}
      </Dialog>
    </UiProvider>
  );
}
