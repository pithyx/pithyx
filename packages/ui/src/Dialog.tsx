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
  /** The heading, also the dialog's accessible name. */
  title: ReactNode;
  /** An icon before the title. */
  icon?: ReactNode;
  /** `danger` for a destructive question; `default` when left out. */
  tone?: 'default' | 'danger';
  /** `alertdialog` for a question that interrupts the user; `dialog` when left out. */
  role?: 'dialog' | 'alertdialog';
  /** `medium` for longer forms such as a user with its rights; `large` for side-by-side content (the pool wizard). */
  size?: 'default' | 'medium' | 'large';
  /** The content. */
  children: ReactNode;
  /** The buttons at the bottom, usually a `Button` each. */
  actions: ReactNode;
}

/**
 * A modal dialog with a title, content and a row of actions. Open it with `isOpen` and `onOpenChange`, or inside a
 * React Aria `DialogTrigger`; Escape and a press outside close it unless `isDismissable` is false.
 *
 * @param props - React Aria's modal overlay props plus title, content and actions.
 * @returns The modal overlay with the dialog.
 *
 * @example
 * ```tsx
 * <Dialog
 *   title="Delete 3 files?"
 *   tone="danger"
 *   role="alertdialog"
 *   isOpen={open}
 *   onOpenChange={setOpen}
 *   actions={<Button variant="danger" onPress={remove}>Delete</Button>}
 * >
 *   They go to the recycle bin.
 * </Dialog>
 * ```
 */
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
      <Modal
        className={cx(
          styles.modal,
          size === 'medium' && styles.medium,
          size === 'large' && styles.large,
        )}
      >
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
