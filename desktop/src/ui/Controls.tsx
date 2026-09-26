import { IconCheck } from '@tabler/icons-react';
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

export function Checkbox({
  children,
  ...props
}: Omit<CheckboxFieldProps, 'className' | 'children'> & { children: ReactNode }) {
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

export function Switch({
  children,
  ...props
}: Omit<SwitchFieldProps, 'className' | 'children'> & { children: ReactNode }) {
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

export function ProgressBar({
  label,
  showValue = true,
  className,
  ...props
}: Omit<AriaProgressBarProps, 'className' | 'children'> & {
  label: ReactNode;
  showValue?: boolean;
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

export function Badge({
  children,
  tone = 'highlight',
  className,
}: {
  children: ReactNode;
  tone?: 'highlight' | 'accent' | 'neutral' | 'danger';
  className?: string;
}) {
  return <span className={cx(styles.badge, styles[tone], className)}>{children}</span>;
}
