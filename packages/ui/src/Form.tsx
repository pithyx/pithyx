import { IconChevronDown, IconInfoCircle } from './icons';
import { useId, type ReactNode } from 'react';
import {
  Button as AriaButton,
  Label,
  ListBox,
  ListBoxItem,
  Popover,
  Provider,
  RadioButton,
  RadioField,
  RadioGroup as AriaRadioGroup,
  RadioGroupContext,
  Select as AriaSelect,
  SelectContext,
  SelectValue,
  Text,
  TextFieldContext,
  type Key,
  type RadioGroupProps as AriaRadioGroupProps,
  type RadioFieldProps,
  type SelectProps as AriaSelectProps,
} from 'react-aria-components';
import { Button } from './Button';
import { cx } from './cx';
import styles from './Form.module.css';
import { useUiTexts } from './UiProvider';

/** The props of {@link FormSection}. */
export interface FormSectionProps {
  /** The section's title. */
  title: ReactNode;
  /** A short muted note after the title, for example the interface the section applies to. */
  note?: ReactNode;
  /** A control at the end of the title row, for example a `Switch` that turns the whole section on or off. */
  end?: ReactNode;
  /** The rows, usually {@link FormRow}s. */
  children?: ReactNode;
  /** Extra class names, added after the section's own. */
  className?: string;
}

/**
 * A section of a settings page (D394): a bordered card with a title row and rows of a label on the left and a control
 * on the right; one column in a narrow container.
 *
 * @param props - The title, an optional note and end control, and the rows.
 * @returns The section.
 *
 * @example
 * ```tsx
 * <FormSection title="IPv4" note="enp1s0">
 *   <FormRow label="IP address"><TextField value={address} onChange={setAddress} /></FormRow>
 * </FormSection>
 * ```
 */
export function FormSection({ title, note, end, children, className }: FormSectionProps) {
  const titleId = useId();
  return (
    <section className={cx(styles.section, className)} aria-labelledby={titleId}>
      <div className={styles.sectionHeader}>
        <h2 id={titleId} className={styles.sectionTitle}>
          {title}
          {note !== undefined && <span className={styles.sectionNote}>{note}</span>}
        </h2>
        {end !== undefined && <div className={styles.sectionEnd}>{end}</div>}
      </div>
      {children !== undefined && <div className={styles.rows}>{children}</div>}
    </section>
  );
}

/** The props of {@link FormRow}. */
export interface FormRowProps {
  /** The label on the left; it names the control. */
  label: ReactNode;
  /** A hint line below the label, announced with the control. */
  hint?: ReactNode;
  /** The control: a `TextField`, `Select` or `RadioGroup` without its own label, or any other content. */
  children: ReactNode;
}

/**
 * A row of a {@link FormSection}: the label with an optional hint on the left, the control on the right (at most
 * 360 px wide). A `TextField`, `Select` or `RadioGroup` inside takes the row's label and hint as its accessible name and
 * description, so leave out its own `label`; any other control needs its own name.
 *
 * @param props - The label, the hint and the control.
 * @returns The row.
 */
export function FormRow({ label, hint, children }: FormRowProps) {
  const labelId = useId();
  const hintId = useId();
  const field = {
    'aria-labelledby': labelId,
    ...(hint === undefined ? {} : { 'aria-describedby': hintId }),
  };
  return (
    <div className={styles.row}>
      <div className={styles.rowLabel}>
        <span id={labelId}>{label}</span>
        {hint !== undefined && (
          <span id={hintId} className={styles.hint}>
            {hint}
          </span>
        )}
      </div>
      <div className={styles.control}>
        <Provider
          values={[
            [TextFieldContext, field],
            [SelectContext, field],
            [RadioGroupContext, field],
          ]}
        >
          {children}
        </Provider>
      </div>
    </div>
  );
}

/** The props of {@link ActionBar}. */
export interface ActionBarProps {
  /** One line about what applying does, for example that the box falls back after 90 seconds. */
  message?: ReactNode;
  /** Applies the changes. */
  onApply: () => void;
  /** Drops the changes. */
  onDiscard: () => void;
  /** Whether there are changes; both buttons are disabled without. True when left out. */
  isDirty?: boolean;
  /** Whether applying runs; Apply shows it and Discard is disabled. */
  isPending?: boolean;
  /** The label of the apply button; the package's "Apply" when left out. */
  applyLabel?: string;
  /** The label of the discard button; the package's "Discard" when left out. */
  discardLabel?: string;
}

/**
 * The bar fixed at the bottom of a page whose changes need applying (D394): one line about what applying does, then
 * **Discard** and **Apply**. Put it in the `footer` of an `AppFrame`. A page whose settings apply at once has none.
 *
 * @param props - The message, the handlers and the state.
 * @returns The action bar.
 */
export function ActionBar({
  message,
  onApply,
  onDiscard,
  isDirty = true,
  isPending = false,
  applyLabel,
  discardLabel,
}: ActionBarProps) {
  const texts = useUiTexts();
  return (
    <div className={styles.actionBar}>
      {message !== undefined && (
        <p className={styles.actionMessage}>
          <IconInfoCircle className={styles.actionIcon} size={16} stroke={1.75} aria-hidden />
          <span>{message}</span>
        </p>
      )}
      <div className={styles.actions}>
        <Button size="sm" onPress={onDiscard} isDisabled={!isDirty || isPending}>
          {discardLabel ?? texts.actionBar.discard}
        </Button>
        <Button
          size="sm"
          variant="primary"
          onPress={onApply}
          isDisabled={!isDirty}
          isPending={isPending}
        >
          {applyLabel ?? texts.actionBar.apply}
        </Button>
      </div>
    </div>
  );
}

/** The props of {@link RadioGroup}. */
export interface RadioGroupProps extends Omit<AriaRadioGroupProps, 'className' | 'children'> {
  /** The visible label; leave it out inside a {@link FormRow}, which names the group. */
  label?: ReactNode;
  /** A hint below the choices, announced with the group. */
  description?: ReactNode;
  /** The {@link Radio} choices. */
  children: ReactNode;
  /** Extra class names, added after the group's own. */
  className?: string;
}

/**
 * Radio buttons for two to four choices (D394); use a {@link Select} beyond that. Side by side unless
 * `orientation` is `vertical`.
 *
 * @param props - React Aria's radio group props (`value`, `onChange`, `isDisabled`) plus label and hint.
 * @returns The radio group.
 *
 * @example
 * ```tsx
 * <RadioGroup label="Address" value={mode} onChange={setMode}>
 *   <Radio value="dhcp">Automatic (DHCP)</Radio>
 *   <Radio value="manual">Manual</Radio>
 * </RadioGroup>
 * ```
 */
export function RadioGroup({
  label,
  description,
  children,
  className,
  orientation = 'horizontal',
  ...props
}: RadioGroupProps) {
  return (
    <AriaRadioGroup
      {...props}
      orientation={orientation}
      className={cx(styles.radioGroup, className)}
    >
      {label !== undefined && <Label className={styles.label}>{label}</Label>}
      <div className={styles.radios}>{children}</div>
      {description !== undefined && (
        <Text slot="description" className={styles.description}>
          {description}
        </Text>
      )}
    </AriaRadioGroup>
  );
}

/** The props of {@link Radio}. */
export interface RadioProps extends Omit<RadioFieldProps, 'className' | 'children'> {
  /** The choice's label. */
  children: ReactNode;
}

/**
 * A choice of a {@link RadioGroup}.
 *
 * @param props - React Aria's radio props with its `value` and the label.
 * @returns The radio button with its label.
 */
export function Radio({ children, ...props }: RadioProps) {
  return (
    <RadioField {...props} className={styles.radioField}>
      <RadioButton className={styles.radio}>
        <span className={styles.circle} aria-hidden />
        {children}
      </RadioButton>
    </RadioField>
  );
}

/** A choice of a {@link Select}. */
export interface SelectOption {
  /** The value. */
  id: Key;
  /** The visible name. */
  label: string;
}

/** The props of {@link Select}. */
export interface SelectProps extends Omit<
  AriaSelectProps<SelectOption>,
  'className' | 'children' | 'items'
> {
  /** The visible label; leave it out inside a {@link FormRow}, which names the select. */
  label?: ReactNode;
  /** A hint below the button, announced with it. */
  description?: ReactNode;
  /** The choices in order. */
  options: readonly SelectOption[];
  /** Whether the value is shown in the monospace font, for addresses and codes. */
  monospace?: boolean;
  /** Extra class names, added after the select's own. */
  className?: string;
}

/**
 * A select for five or more choices (D394); use a {@link RadioGroup} for fewer.
 *
 * @param props - React Aria's select props (`value`, `onChange`, `isDisabled`) plus label, hint and the options.
 * @returns The select with its popover.
 *
 * @example
 * ```tsx
 * <Select label="Time zone" options={zones} value={zone} onChange={setZone} />
 * ```
 */
export function Select({
  label,
  description,
  options,
  monospace = false,
  className,
  placeholder,
  ...props
}: SelectProps) {
  const texts = useUiTexts();
  return (
    <AriaSelect
      {...props}
      placeholder={placeholder ?? texts.select.placeholder}
      className={cx(styles.select, className)}
    >
      {label !== undefined && <Label className={styles.label}>{label}</Label>}
      <AriaButton className={cx(styles.selectButton, monospace && styles.monospace)}>
        <SelectValue className={styles.selectValue} />
        <IconChevronDown size={16} stroke={1.75} aria-hidden />
      </AriaButton>
      {description !== undefined && (
        <Text slot="description" className={styles.description}>
          {description}
        </Text>
      )}
      <Popover className={styles.popover}>
        <ListBox className={styles.listBox} items={options}>
          {(option) => (
            <ListBoxItem
              id={option.id}
              textValue={option.label}
              className={cx(styles.option, monospace && styles.monospace)}
            >
              {option.label}
            </ListBoxItem>
          )}
        </ListBox>
      </Popover>
    </AriaSelect>
  );
}
