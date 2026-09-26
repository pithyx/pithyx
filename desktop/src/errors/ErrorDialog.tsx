import { useEffect, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from './ErrorNotice';
import { closeError, useErrorStore } from './errorStore';

export function ErrorDialog() {
  const { t } = useTranslation();
  const error = useErrorStore((state) => state.current);
  const dialog = useRef<HTMLDialogElement>(null);

  // Syncs the store with the native modal dialog, which handles focus and Escape.
  useEffect(() => {
    const element = dialog.current;
    if (error !== null && element !== null && !element.open) {
      element.showModal();
    }
  }, [error]);

  return (
    <dialog
      ref={dialog}
      className="dialog"
      aria-labelledby="error-dialog-title"
      onClose={closeError}
    >
      <h2 id="error-dialog-title" className="dialog__title">
        {t('error.title')}
      </h2>
      {error !== null && <ErrorNotice error={error} />}
      <form method="dialog" className="dialog__actions">
        <button type="submit" className="button">
          {t('error.close')}
        </button>
      </form>
    </dialog>
  );
}
