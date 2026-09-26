import { IconCheck, IconChevronRight } from '@tabler/icons-react';
import type { ReactNode } from 'react';
import {
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
}

export function MenuItem({ icon, children, tone = 'default', ...props }: MenuItemProps) {
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
