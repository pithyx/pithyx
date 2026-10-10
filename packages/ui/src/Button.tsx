import type { ReactNode } from 'react';
import { Button as AriaButton, type ButtonProps as AriaButtonProps } from 'react-aria-components';
import styles from './Button.module.css';
import { cx } from './cx';
import { Tooltip, TooltipTrigger } from './Tooltip';

/** How much a button stands out: `primary` for the main action, `danger` for a destructive one. */
export type ButtonVariant = 'primary' | 'secondary' | 'quiet' | 'danger';

/** The props of {@link Button}: React Aria's button props plus the look. */
export interface ButtonProps extends Omit<AriaButtonProps, 'className' | 'children'> {
  /** How much the button stands out; `secondary` when left out. */
  variant?: ButtonVariant;
  /** `sm` for dense rows and toolbars; `md` when left out. */
  size?: 'md' | 'sm';
  /** Extra class names, added after the button's own. */
  className?: string;
  /** The label, an icon or both. */
  children?: ReactNode;
}

/**
 * A button in the desktop's look. Use `onPress` instead of `onClick`, so keyboard, touch and screen readers act alike,
 * and `isPending` for an action that runs.
 *
 * @param props - React Aria's button props plus `variant` and `size`.
 * @returns The button element.
 *
 * @example
 * ```tsx
 * <Button variant="primary" onPress={save}>Save</Button>
 * ```
 */
export function Button({ variant = 'secondary', size = 'md', className, ...props }: ButtonProps) {
  return (
    <AriaButton
      {...props}
      className={cx(styles.button, styles[variant], size === 'sm' && styles.small, className)}
    />
  );
}

/** The props of {@link IconButton}. */
export interface IconButtonProps extends Omit<ButtonProps, 'aria-label'> {
  /** Accessible name, also shown as tooltip. */
  label: string;
  /** Hides the tooltip where the label is already visible nearby. */
  tooltip?: boolean;
}

/**
 * A button that shows only an icon; its label is announced and shown as a tooltip. `quiet` when no `variant` is set.
 *
 * @param props - The props of {@link Button} plus `label` and `tooltip`.
 * @returns The button, wrapped in a tooltip trigger unless `tooltip` is false.
 *
 * @example
 * ```tsx
 * <IconButton label="Delete" onPress={remove}><IconTrash size={18} /></IconButton>
 * ```
 */
export function IconButton({
  label,
  tooltip = true,
  variant = 'quiet',
  className,
  ...props
}: IconButtonProps) {
  const button = (
    <Button
      {...props}
      aria-label={label}
      variant={variant}
      className={cx(styles.icon, className)}
    />
  );
  if (!tooltip) {
    return button;
  }
  return (
    <TooltipTrigger delay={600} closeDelay={0}>
      {button}
      <Tooltip>{label}</Tooltip>
    </TooltipTrigger>
  );
}
