import type { ReactNode } from 'react';
import { Button as AriaButton, type ButtonProps as AriaButtonProps } from 'react-aria-components';
import styles from './Button.module.css';
import { cx } from './cx';
import { Tooltip, TooltipTrigger } from './Tooltip';

export type ButtonVariant = 'primary' | 'secondary' | 'quiet' | 'danger';

export interface ButtonProps extends Omit<AriaButtonProps, 'className' | 'children'> {
  variant?: ButtonVariant;
  size?: 'md' | 'sm';
  className?: string;
  children?: ReactNode;
}

export function Button({ variant = 'secondary', size = 'md', className, ...props }: ButtonProps) {
  return (
    <AriaButton
      {...props}
      className={cx(styles.button, styles[variant], size === 'sm' && styles.small, className)}
    />
  );
}

export interface IconButtonProps extends Omit<ButtonProps, 'aria-label'> {
  /** Accessible name, also shown as tooltip. */
  label: string;
  /** Hides the tooltip where the label is already visible nearby. */
  tooltip?: boolean;
}

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
