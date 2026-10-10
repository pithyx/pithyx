import { IconCheck, IconChevronRight } from './icons';
import { useRef, type ReactNode } from 'react';
import {
  Keyboard,
  Menu as AriaMenu,
  MenuItem as AriaMenuItem,
  MenuTrigger as AriaMenuTrigger,
  Popover,
  Separator,
  type MenuItemProps as AriaMenuItemProps,
  type MenuProps as AriaMenuProps,
  type PopoverProps,
} from 'react-aria-components';
import { cx } from './cx';
import styles from './Menu.module.css';

/**
 * Opens a menu from its trigger: the first child is the trigger (a `Button` or `IconButton`), the second a
 * {@link MenuPopover} with the {@link Menu}. It is React Aria's `MenuTrigger`; its props (`isOpen`, `onOpenChange`,
 * `trigger`) are documented there.
 *
 * @param props - React Aria's menu trigger props and the two children.
 * @returns The trigger with its menu.
 */
export const MenuTrigger = AriaMenuTrigger;

/**
 * The popover a {@link Menu} opens in, next to its trigger inside a `MenuTrigger`.
 *
 * @param props - React Aria's popover props (`placement`, `offset`).
 * @returns The popover.
 */
export function MenuPopover({
  className,
  ...props
}: Omit<PopoverProps, 'className'> & {
  /** Extra class names, added after the popover's own. */
  className?: string;
}) {
  return <Popover offset={6} {...props} className={cx(styles.popover, className)} />;
}

/**
 * A menu of {@link MenuItem}s and {@link MenuSeparator}s, with keyboard and touch handling.
 *
 * @param props - React Aria's menu props (`onAction`, `selectionMode`, `items`).
 * @returns The menu.
 *
 * @example
 * ```tsx
 * <MenuTrigger>
 *   <IconButton label="More"><IconDots size={18} /></IconButton>
 *   <MenuPopover>
 *     <Menu onAction={(id) => run(String(id))}>
 *       <MenuItem id="rename" shortcut="F2">Rename</MenuItem>
 *       <MenuSeparator />
 *       <MenuItem id="delete" tone="danger">Delete</MenuItem>
 *     </Menu>
 *   </MenuPopover>
 * </MenuTrigger>
 * ```
 */
export function Menu<T extends object>({
  className,
  ...props
}: Omit<AriaMenuProps<T>, 'className'> & {
  /** Extra class names, added after the menu's own. */
  className?: string;
}) {
  return <AriaMenu {...props} className={cx(styles.menu, className)} />;
}

/** The props of {@link MenuItem}. */
export interface MenuItemProps extends Omit<AriaMenuItemProps, 'className' | 'children'> {
  /** An icon before the label. */
  icon?: ReactNode;
  /** The label; a string is also the text for type-ahead. */
  children: ReactNode;
  /** `danger` for a destructive entry; `default` when left out. */
  tone?: 'default' | 'danger';
  /** Keyboard shortcut shown at the end of the item. */
  shortcut?: string;
}

/**
 * One entry of a menu, with an optional icon and key hint. It shows a check mark in a menu with a `selectionMode` and
 * a chevron when it opens a submenu.
 *
 * @param props - React Aria's menu item props (`id`, `onAction`, `isDisabled`, `textValue`) plus icon, tone and
 * shortcut.
 * @returns The menu item.
 */
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

/**
 * A line between groups of menu entries.
 *
 * @returns The separator.
 */
export function MenuSeparator() {
  return <Separator className={styles.separator} />;
}

/**
 * React Aria runs a menu item's action when a mouse press that began elsewhere, on the menu's trigger, is released
 * over the item (press, drag, release). A menu that opens under the pointer can then act on a plain click of its
 * trigger. Destructive items take their action only from a press that started on them: spread `pressProps` on the
 * item and ask `confirm()` in the menu's action handler.
 *
 * @returns The props for the item and the check for the action handler.
 *
 * @example
 * ```tsx
 * const deliberate = useDeliberatePress();
 * <Menu onAction={(id) => id === 'delete' && deliberate.confirm() && remove()}>
 *   <MenuItem id="delete" tone="danger" {...deliberate.pressProps}>Delete</MenuItem>
 * </Menu>
 * ```
 */
export function useDeliberatePress() {
  const armed = useRef(false);
  return {
    /** Spread on the destructive item. */
    pressProps: {
      /** Arms the item when a press starts on it. */
      onPressStart: () => {
        armed.current = true;
      },
    },
    /** Whether the action comes from a press that started on the item; disarms it. */
    confirm: () => {
      const deliberate = armed.current;
      armed.current = false;
      return deliberate;
    },
  };
}
