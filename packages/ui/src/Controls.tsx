import { IconCheck } from './icons';
import type { ReactNode } from 'react';
import {
  CheckboxButton,
  CheckboxField,
  Label,
  ProgressBar as AriaProgressBar,
  SwitchButton,
  SwitchField,
  type CheckboxFieldProps,
  type ProgressBarProps as AriaProgressBarProps,
  type SwitchFieldProps,
} from 'react-aria-components';
import styles from './Controls.module.css';
import { cx } from './cx';

/**
 * A checkbox with its label.
 *
 * @param props - React Aria's checkbox field props (`isSelected`, `onChange`, `isDisabled`) plus the label.
 * @returns The checkbox with its label.
 */
export function Checkbox({
  children,
  ...props
}: Omit<CheckboxFieldProps, 'className' | 'children'> & {
  /** The label, also the accessible name. */
  children: ReactNode;
}) {
  return (
    <CheckboxField {...props} className={styles.field}>
      <CheckboxButton className={styles.checkbox}>
        <span className={styles.box} aria-hidden>
          <IconCheck size={14} stroke={3} />
        </span>
        {children}
      </CheckboxButton>
    </CheckboxField>
  );
}

/**
 * An on and off switch with its label, for settings that apply at once.
 *
 * @param props - React Aria's switch field props (`isSelected`, `onChange`, `isDisabled`) plus the label.
 * @returns The switch with its label.
 */
export function Switch({
  children,
  ...props
}: Omit<SwitchFieldProps, 'className' | 'children'> & {
  /** The label, also the accessible name. */
  children: ReactNode;
}) {
  return (
    <SwitchField {...props} className={styles.field}>
      <SwitchButton className={styles.switch}>
        <span className={styles.track} aria-hidden>
          <span className={styles.thumb} />
        </span>
        {children}
      </SwitchButton>
    </SwitchField>
  );
}

/**
 * A labelled progress bar that shows its value as text unless `showValue` is false. Without `value` it is
 * indeterminate only when `isIndeterminate` is set.
 *
 * @param props - React Aria's progress bar props (`value`, `minValue`, `maxValue`, `formatOptions`) plus the label.
 * @returns The progress bar with its label.
 */
export function ProgressBar({
  label,
  showValue = true,
  className,
  ...props
}: Omit<AriaProgressBarProps, 'className' | 'children'> & {
  /** The label above the bar, also the accessible name. */
  label: ReactNode;
  /** Whether the value is shown as text next to the label; true when left out. */
  showValue?: boolean;
  /** Extra class names, added after the bar's own. */
  className?: string;
}) {
  return (
    <AriaProgressBar {...props} className={cx(styles.progress, className)}>
      {({ percentage, valueText }) => (
        <>
          <Label className={styles.progressLabel}>{label}</Label>
          {showValue && <span className={styles.progressValue}>{valueText}</span>}
          <span className={styles.bar}>
            <span className={styles.fill} style={{ width: `${String(percentage ?? 0)}%` }} />
          </span>
        </>
      )}
    </AriaProgressBar>
  );
}

/**
 * A small count or status next to an item, for example in a list or on a tab. It is plain text for screen readers;
 * give it words where a number alone says too little.
 *
 * @param props - The content and the tone.
 * @returns The badge element.
 */
export function Badge({
  children,
  tone = 'highlight',
  className,
}: {
  /** The count or the status text. */
  children: ReactNode;
  /** The color: `highlight` (amber, the default), `accent`, `neutral` or `danger`. */
  tone?: 'highlight' | 'accent' | 'neutral' | 'danger';
  /** Extra class names, added after the badge's own. */
  className?: string;
}) {
  return <span className={cx(styles.badge, styles[tone], className)}>{children}</span>;
}
