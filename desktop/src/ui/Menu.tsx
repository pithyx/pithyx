import { IconCheck, IconChevronRight } from './icons';
import { useRef, type ReactNode } from 'react';
import {
  Keyboard,
  Menu as AriaMenu,
  MenuItem as AriaMenuItem,
  Popover,
  Separator,
  type MenuItemProps as AriaMenuItemProps,
  type MenuProps as AriaMenuProps,
  type PopoverProps,
} from 'react-aria-components';
import { cx } from './cx';
import styles from './Menu.module.css';

export { MenuTrigger } from 'react-aria-components';

export function MenuPopover({
  className,
  ...props
}: Omit<PopoverProps, 'className'> & { className?: string }) {
  return <Popover offset={6} {...props} className={cx(styles.popover, className)} />;
}

export function Menu<T extends object>({
  className,
  ...props
}: Omit<AriaMenuProps<T>, 'className'> & { className?: string }) {
  return <AriaMenu {...props} className={cx(styles.menu, className)} />;
}

export interface MenuItemProps extends Omit<AriaMenuItemProps, 'className' | 'children'> {
  icon?: ReactNode;
  children: ReactNode;
  tone?: 'default' | 'danger';
  /** Keyboard shortcut shown at the end of the item. */
  shortcut?: string;
}

export function MenuItem({ icon, children, tone = 'default', shortcut, ...props }: MenuItemProps) {
  const textValue = typeof children === 'string' ? children : props.textValue;
  return (
    <AriaMenuItem
      {...props}
      {...(textValue === undefined ? {} : { textValue })}
      className={cx(styles.item, tone === 'danger' && styles.danger)}
    >
      {({ isSelected, selectionMode, hasSubmenu }) => (
        <>
          {icon !== undefined && <span className={styles.icon}>{icon}</span>}
          <span className={styles.label}>{children}</span>
          {shortcut !== undefined && <Keyboard className={styles.shortcut}>{shortcut}</Keyboard>}
          {selectionMode !== 'none' && (
            <span className={styles.check} aria-hidden>
              {isSelected && <IconCheck size={16} stroke={2} />}
            </span>
          )}
          {hasSubmenu && (
            <span className={styles.icon} aria-hidden>
              <IconChevronRight size={16} stroke={1.75} />
            </span>
          )}
        </>
      )}
    </AriaMenuItem>
  );
}

export function MenuSeparator() {
  return <Separator className={styles.separator} />;
}

/**
 * React Aria runs a menu item's action when a mouse press that began elsewhere, on the menu's trigger, is released
 * over the item (press, drag, release). A menu that opens under the pointer can then act on a plain click of its
 * trigger. Destructive items take their action only from a press that started on them: spread `pressProps` on the
 * item and ask `confirm()` in the menu's action handler.
 */
export function useDeliberatePress() {
  const armed = useRef(false);
  return {
    pressProps: {
      onPressStart: () => {
        armed.current = true;
      },
    },
    confirm: () => {
      const deliberate = armed.current;
      armed.current = false;
      return deliberate;
    },
  };
}
