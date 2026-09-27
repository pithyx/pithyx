import {
  useId,
  useState,
  type ButtonHTMLAttributes,
  type InputHTMLAttributes,
  type ReactNode,
  type Ref,
} from 'react';
import { useTranslation } from 'react-i18next';
import { languageName, languages, setLanguage, storedLanguage, type Language } from '../i18n';
import buttonStyles from '../ui/Button.module.css';
import controlStyles from '../ui/Controls.module.css';
import { cx } from '../ui/cx';
import { IconCheck, IconEye, IconEyeOff, IconLanguage, IconLoader2 } from '../ui/icons';
import fieldStyles from '../ui/TextField.module.css';
import styles from './Auth.module.css';

// Native form controls for setup and sign-in. They share the design system's styles with the React Aria primitives
// in ui/, but keep React Aria out of the first chunk: the sign-in screen must load fast on a slow connection.

export interface FieldProps extends Omit<InputHTMLAttributes<HTMLInputElement>, 'className'> {
  label: string;
  description?: string | undefined;
  /** Shown below the field and linked to it; the field is marked invalid while it is set. */
  error?: string | undefined;
  inputClassName?: string;
  trailing?: ReactNode;
  ref?: Ref<HTMLInputElement>;
}

export function Field({
  label,
  description,
  error,
  inputClassName,
  trailing,
  ref,
  id,
  ...input
}: FieldProps) {
  const generated = useId();
  const inputId = id ?? generated;
  const describedBy = [
    error === undefined ? null : `${inputId}-error`,
    description === undefined ? null : `${inputId}-description`,
  ]
    .filter((value) => value !== null)
    .join(' ');
  return (
    <div className={fieldStyles.field} data-invalid={error === undefined ? undefined : true}>
      <label htmlFor={inputId} className={fieldStyles.label}>
        {label}
      </label>
      <div className={fieldStyles.frame}>
        <input
          ref={ref}
          id={inputId}
          className={cx(fieldStyles.input, inputClassName)}
          aria-invalid={error === undefined ? undefined : true}
          aria-describedby={describedBy === '' ? undefined : describedBy}
          {...input}
        />
        {trailing}
      </div>
      {description !== undefined && (
        <p id={`${inputId}-description`} className={fieldStyles.description}>
          {description}
        </p>
      )}
      {error !== undefined && (
        <p id={`${inputId}-error`} className={fieldStyles.error}>
          {error}
        </p>
      )}
    </div>
  );
}

export function PasswordField(props: Omit<FieldProps, 'type' | 'trailing'>) {
  const { t } = useTranslation();
  const [visible, setVisible] = useState(false);
  return (
    <Field
      {...props}
      type={visible ? 'text' : 'password'}
      spellCheck={false}
      trailing={
        <button
          type="button"
          className={fieldStyles.reveal}
          aria-label={visible ? t('ui.password.hide') : t('ui.password.show')}
          aria-pressed={visible}
          onClick={() => {
            setVisible(!visible);
          }}
        >
          {visible ? <IconEyeOff size={18} stroke={1.75} /> : <IconEye size={18} stroke={1.75} />}
        </button>
      }
    />
  );
}

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: 'primary' | 'secondary' | 'quiet';
  size?: 'md' | 'sm';
  /**
   * Shows a spinner and blocks further presses without disabling the button, so keyboard focus and the screen
   * reader's position stay where they are.
   */
  pending?: boolean;
}

export function Button({
  variant = 'secondary',
  size = 'md',
  pending = false,
  className,
  children,
  onClick,
  type = 'button',
  ...props
}: ButtonProps) {
  return (
    <button
      {...props}
      type={type}
      data-native
      className={cx(
        buttonStyles.button,
        buttonStyles[variant],
        size === 'sm' && buttonStyles.small,
        className,
      )}
      aria-busy={pending || undefined}
      aria-disabled={pending || undefined}
      // Implicit submission with Enter also arrives as a click, so this covers double submits from the keyboard.
      onClick={(event) => {
        if (pending) {
          event.preventDefault();
          return;
        }
        onClick?.(event);
      }}
    >
      {pending && <IconLoader2 size={18} stroke={2} className={buttonStyles.spinner} aria-hidden />}
      {children}
    </button>
  );
}

export function Checkbox({
  checked,
  onChange,
  children,
}: {
  checked: boolean;
  onChange: (checked: boolean) => void;
  children: ReactNode;
}) {
  return (
    <label className={controlStyles.checkbox}>
      <input
        type="checkbox"
        className="visually-hidden"
        checked={checked}
        onChange={(event) => {
          onChange(event.target.checked);
        }}
      />
      <span className={controlStyles.box} aria-hidden>
        <IconCheck size={14} stroke={3} />
      </span>
      {children}
    </label>
  );
}

/** Language switch below the sign-in card: a native select, kept on this device. */
export function LanguageSelect() {
  const { t } = useTranslation();
  const [choice, setChoice] = useState<Language | 'auto'>(storedLanguage() ?? 'auto');
  return (
    <label className={styles.language}>
      <IconLanguage size={16} stroke={1.75} aria-hidden />
      <span className="visually-hidden">{t('menu.language')}</span>
      <select
        className={styles.languageSelect}
        value={choice}
        onChange={(event) => {
          const language = languages.find((code) => code === event.target.value) ?? null;
          setChoice(language ?? 'auto');
          void setLanguage(language);
        }}
      >
        <option value="auto">{t('menu.languageAuto')}</option>
        {languages.map((code) => (
          <option key={code} value={code} lang={code}>
            {languageName(code)}
          </option>
        ))}
      </select>
    </label>
  );
}
