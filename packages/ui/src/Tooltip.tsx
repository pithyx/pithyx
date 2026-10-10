import {
  Tooltip as AriaTooltip,
  TooltipTrigger as AriaTooltipTrigger,
  type TooltipProps as AriaTooltipProps,
} from 'react-aria-components';
import { cx } from './cx';
import styles from './Tooltip.module.css';

/**
 * Shows a {@link Tooltip} for its first child on hover and keyboard focus. It is React Aria's `TooltipTrigger`; its
 * props (`delay`, `closeDelay`, `isDisabled`) are documented there.
 *
 * @param props - React Aria's tooltip trigger props, the trigger and the tooltip.
 * @returns The trigger with its tooltip.
 */
export const TooltipTrigger = AriaTooltipTrigger;

/** The props of {@link Tooltip}. */
export interface TooltipProps extends Omit<AriaTooltipProps, 'className'> {
  /** Extra class names, added after the tooltip's own. */
  className?: string;
}

/**
 * A short text next to its trigger; wrap both in a `TooltipTrigger`. A tooltip shows on hover and keyboard focus,
 * never on touch, so it must not carry anything the user needs.
 *
 * @param props - React Aria's tooltip props (`placement`, `offset`) and the text.
 * @returns The tooltip.
 *
 * @example
 * ```tsx
 * <TooltipTrigger>
 *   <Button>Sync</Button>
 *   <Tooltip>Last synced 5 minutes ago</Tooltip>
 * </TooltipTrigger>
 * ```
 */
export function Tooltip({ className, ...props }: TooltipProps) {
  return <AriaTooltip offset={6} {...props} className={cx(styles.tooltip, className)} />;
}
