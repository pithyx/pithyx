import { IconAlertTriangle } from '@tabler/icons-react';
import { useTranslation } from 'react-i18next';
import { Button } from '../ui/Button';
import { Dialog } from '../ui/Dialog';
import type { DescribedError } from './describe';
import { RequestId, useErrorMessage } from './ErrorNotice';
import { closeError, useErrorStore } from './errorStore';

function ErrorContent({ error }: { error: DescribedError }) {
  const message = useErrorMessage(error);
  return (
    <>
      <p>{message}</p>
      {error.requestId !== '' && <RequestId requestId={error.requestId} />}
    </>
  );
}

/** Errors that do not belong to a form: the translated message and the request id. */
export function ErrorDialog() {
  const { t } = useTranslation();
  const error = useErrorStore((state) => state.current);
  return (
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
  );
}
