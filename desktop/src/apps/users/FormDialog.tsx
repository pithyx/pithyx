import { useId, type ReactNode, type SyntheticEvent } from 'react';
import { useTranslation } from 'react-i18next';
import { Button, Dialog } from '@pithyx/ui';
import styles from './Users.module.css';

/** A dialog around one form, with Cancel and a submit button, for the Users page. */
export function FormDialog({
  title,
  submit,
  danger = false,
  disabled = false,
  size = 'default',
  onClose,
  onSubmit,
  children,
}: {
  title: string;
  submit: string;
  danger?: boolean;
  disabled?: boolean;
  /** `medium` for the forms with memberships and rights. */
  size?: 'default' | 'medium';
  onClose: () => void;
  onSubmit: (event: SyntheticEvent<HTMLFormElement>) => void;
  children: ReactNode;
}) {
  const { t } = useTranslation('users');
  const formId = useId();
  return (
    <Dialog
      isOpen
      isDismissable
      size={size}
      tone={danger ? 'danger' : 'default'}
      onOpenChange={(open) => {
        if (!open) {
          onClose();
        }
      }}
      title={title}
      actions={
        <>
          <Button onPress={onClose}>{t('cancel')}</Button>
          <Button
            type="submit"
            form={formId}
            variant={danger ? 'danger' : 'primary'}
            isDisabled={disabled}
          >
            {submit}
          </Button>
        </>
      }
    >
      <form id={formId} className={styles.form} onSubmit={onSubmit} noValidate>
        {children}
      </form>
    </Dialog>
  );
}
