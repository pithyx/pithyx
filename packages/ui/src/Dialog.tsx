import type { ReactNode } from 'react';
import {
  Dialog as AriaDialog,
  Heading,
  Modal,
  ModalOverlay,
  type ModalOverlayProps,
} from 'react-aria-components';
import { cx } from './cx';
import styles from './Dialog.module.css';

/** The props of {@link Dialog}: React Aria's modal props plus title, content and actions. */
export interface DialogProps extends Omit<ModalOverlayProps, 'className' | 'children'> {
  title: ReactNode;
  icon?: ReactNode;
  tone?: 'default' | 'danger';
  role?: 'dialog' | 'alertdialog';
  /** Wide dialogs for side-by-side content such as the pool wizard's layout cards. */
  size?: 'default' | 'large';
  children: ReactNode;
  actions: ReactNode;
}

/** A modal dialog with a title, content and a row of actions. */
export function Dialog({
  title,
  icon,
  tone = 'default',
  role = 'dialog',
  size = 'default',
  children,
  actions,
  ...props
}: DialogProps) {
  return (
    <ModalOverlay {...props} className={styles.overlay}>
      <Modal className={cx(styles.modal, size === 'large' && styles.large)}>
        <AriaDialog role={role} className={cx(styles.dialog, tone === 'danger' && styles.danger)}>
          <div className={styles.header}>
            {icon !== undefined && <span className={styles.icon}>{icon}</span>}
            <Heading slot="title" className={styles.title}>
              {title}
            </Heading>
          </div>
          <div className={styles.body}>{children}</div>
          <div className={styles.actions}>{actions}</div>
        </AriaDialog>
      </Modal>
    </ModalOverlay>
  );
}
