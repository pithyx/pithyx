import { IconEye, IconEyeOff, IconSearch, IconX } from './icons';
import { useState, type ReactNode } from 'react';
import {
  Button as AriaButton,
  FieldError,
  Input,
  Label,
  SearchField as AriaSearchField,
  Text,
  TextField as AriaTextField,
  type SearchFieldProps as AriaSearchFieldProps,
  type TextFieldProps as AriaTextFieldProps,
} from 'react-aria-components';
import { cx } from './cx';
import styles from './TextField.module.css';
import { useUiTexts } from './UiProvider';

/** The props of {@link TextField}: React Aria's text field props plus label, hints and an error. */
export interface TextFieldProps extends Omit<AriaTextFieldProps, 'className' | 'children'> {
  /** The visible label, also the accessible name. */
  label: ReactNode;
  /** A hint below the input, announced with it. */
  description?: ReactNode;
  /** The text shown while the field is invalid (`isInvalid` or a failed `validate`). */
  errorMessage?: ReactNode;
  /** A hint inside the empty input; never a replacement for the label. */
  placeholder?: string;
  /** Extra class names for the field, added after its own. */
  className?: string;
  /** Extra class names for the input element. */
  inputClassName?: string;
  /** "none" for user names and codes, so phones do not capitalize the first letter. */
  autoCapitalize?: 'none' | 'characters';
  /** Trailing content inside the input frame, for example a reveal button. */
  trailing?: ReactNode;
}

/**
 * A labelled text input with an optional description and error message. Set `autoComplete` as the form needs it
 * (`username`, `email`), so password managers and browsers fill it in.
 *
 * @param props - React Aria's text field props (`value`, `onChange`, `isRequired`, `validate`) plus label, hints and
 * error.
 * @returns The field with label, input, description and error.
 *
 * @example
 * ```tsx
 * <TextField label="Name" value={name} onChange={setName} isRequired />
 * ```
 */
export function TextField({
  label,
  description,
  errorMessage,
  placeholder,
  className,
  inputClassName,
  autoCapitalize,
  trailing,
  ...props
}: TextFieldProps) {
  return (
    <AriaTextField {...props} className={cx(styles.field, className)}>
      <Label className={styles.label}>{label}</Label>
      <div className={styles.frame}>
        <Input
          className={cx(styles.input, inputClassName)}
          placeholder={placeholder ?? ''}
          {...(autoCapitalize === undefined ? {} : { autoCapitalize })}
        />
        {trailing}
      </div>
      {description !== undefined && (
        <Text slot="description" className={styles.description}>
          {description}
        </Text>
      )}
      <FieldError className={styles.error}>{errorMessage}</FieldError>
    </AriaTextField>
  );
}

/** The props of {@link PasswordField}; `autoComplete` tells password managers what to do. */
export interface PasswordFieldProps extends Omit<TextFieldProps, 'type' | 'trailing'> {
  /** `current-password` to sign in, `new-password` to set one. */
  autoComplete: 'current-password' | 'new-password';
}

/**
 * A password input with a button that shows or hides the password. The button's label comes from the package's own
 * texts in the language of the nearest `UiProvider`.
 *
 * @param props - The props of {@link TextField} without `type` and `trailing`, plus the required `autoComplete`.
 * @returns The password field.
 */
export function PasswordField(props: PasswordFieldProps) {
  const texts = useUiTexts();
  const [visible, setVisible] = useState(false);
  return (
    <TextField
      {...props}
      type={visible ? 'text' : 'password'}
      spellCheck="false"
      trailing={
        <AriaButton
          className={styles.reveal}
          aria-label={visible ? texts.password.hide : texts.password.show}
          aria-pressed={visible}
          onPress={() => {
            setVisible(!visible);
          }}
        >
          {visible ? <IconEyeOff size={18} stroke={1.75} /> : <IconEye size={18} stroke={1.75} />}
        </AriaButton>
      }
    />
  );
}

/**
 * An input for the six-digit code from an authenticator app: numeric keyboard, `one-time-code` autofill and at most
 * seven characters (a space in the middle is allowed).
 *
 * @param props - The props of {@link TextField} without `type`, `inputMode` and `autoComplete`.
 * @returns The code field.
 */
export function OtpField(props: Omit<TextFieldProps, 'type' | 'inputMode' | 'autoComplete'>) {
  return (
    <TextField
      {...props}
      inputMode="numeric"
      autoComplete="one-time-code"
      maxLength={7}
      inputClassName={styles.otp}
    />
  );
}

/** The props of {@link SearchField}; `label` is announced, not shown. */
export interface SearchFieldProps extends Omit<AriaSearchFieldProps, 'className' | 'children'> {
  /** The accessible name, announced but not shown. */
  label: string;
  /** The visible hint inside the empty input. */
  placeholder?: string;
  /** Extra class names, added after the field's own. */
  className?: string;
}

/**
 * A search input whose label is only announced; the placeholder carries the visible hint. Escape and the clear
 * button empty it.
 *
 * @param props - React Aria's search field props (`value`, `onChange`, `onSubmit`) plus label and placeholder.
 * @returns The search field.
 */
export function SearchField({ label, placeholder, className, ...props }: SearchFieldProps) {
  const texts = useUiTexts();
  return (
    <AriaSearchField {...props} aria-label={label} className={cx(styles.search, className)}>
      <IconSearch className={styles.searchIcon} size={18} stroke={1.75} aria-hidden />
      <Input className={styles.searchInput} placeholder={placeholder ?? ''} />
      <AriaButton className={styles.clear} aria-label={texts.search.clear}>
        <IconX size={16} stroke={1.75} />
      </AriaButton>
    </AriaSearchField>
  );
}
