import { IconEye, IconEyeOff, IconSearch, IconX } from '@tabler/icons-react';
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
import { useTranslation } from 'react-i18next';
import { cx } from './cx';
import styles from './TextField.module.css';

export interface TextFieldProps extends Omit<AriaTextFieldProps, 'className' | 'children'> {
  label: ReactNode;
  description?: ReactNode;
  errorMessage?: ReactNode;
  placeholder?: string;
  className?: string;
  inputClassName?: string;
  /** "none" for user names and codes, so phones do not capitalize the first letter. */
  autoCapitalize?: 'none' | 'characters';
  /** Trailing content inside the input frame, for example a reveal button. */
  trailing?: ReactNode;
}

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

export interface PasswordFieldProps extends Omit<TextFieldProps, 'type' | 'trailing'> {
  autoComplete: 'current-password' | 'new-password';
}

export function PasswordField(props: PasswordFieldProps) {
  const { t } = useTranslation();
  const [visible, setVisible] = useState(false);
  return (
    <TextField
      {...props}
      type={visible ? 'text' : 'password'}
      spellCheck="false"
      trailing={
        <AriaButton
          className={styles.reveal}
          aria-label={visible ? t('ui.password.hide') : t('ui.password.show')}
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

/** Six-digit code from an authenticator app. */
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

export interface SearchFieldProps extends Omit<AriaSearchFieldProps, 'className' | 'children'> {
  label: string;
  placeholder?: string;
  className?: string;
}

/** A search input whose label is only announced; the placeholder carries the visible hint. */
export function SearchField({ label, placeholder, className, ...props }: SearchFieldProps) {
  const { t } = useTranslation();
  return (
    <AriaSearchField {...props} aria-label={label} className={cx(styles.search, className)}>
      <IconSearch className={styles.searchIcon} size={18} stroke={1.75} aria-hidden />
      <Input className={styles.searchInput} placeholder={placeholder ?? ''} />
      <AriaButton className={styles.clear} aria-label={t('ui.search.clear')}>
        <IconX size={16} stroke={1.75} />
      </AriaButton>
    </AriaSearchField>
  );
}
