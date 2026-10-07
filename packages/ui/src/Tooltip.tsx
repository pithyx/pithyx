import {
  Tooltip as AriaTooltip,
  type TooltipProps as AriaTooltipProps,
} from 'react-aria-components';
import { cx } from './cx';
import styles from './Tooltip.module.css';

export { TooltipTrigger } from 'react-aria-components';

/** The props of {@link Tooltip}. */
export interface TooltipProps extends Omit<AriaTooltipProps, 'className'> {
  className?: string;
}

/** A short text next to its trigger; wrap both in a `TooltipTrigger`. */
export function Tooltip({ className, ...props }: TooltipProps) {
  return <AriaTooltip offset={6} {...props} className={cx(styles.tooltip, className)} />;
}
