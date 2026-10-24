import {
  useEffect,
  useId,
  useRef,
  useState,
  type ButtonHTMLAttributes,
  type InputHTMLAttributes,
  type ReactNode,
  type Ref,
} from 'react';
import { useTranslation } from 'react-i18next';
import { languageName, languages, setLanguage, storedLanguage, type Language } from '../i18n';
import {
  buttonStyles,
  controlStyles,
  cx,
  textFieldStyles as fieldStyles,
  uiTexts,
} from '@pithyx/ui/native';
import {
  IconCheck,
  IconChevronDown,
  IconEye,
  IconEyeOff,
  IconLanguage,
  IconLoader2,
} from '../ui/icons';
import styles from './Auth.module.css';

// Native form controls for setup and sign-in. They share the styles and texts of @pithyx/ui's React Aria components,
// but keep React Aria out of the first chunk: the sign-in screen must load fast on a slow connection.

export interface FieldProps extends Omit<InputHTMLAttributes<HTMLInputElement>, 'className'> {
  label: string;
  description?: ReactNode;
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
        <div id={`${inputId}-description`} className={fieldStyles.description}>
          {description}
        </div>
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
  const { i18n } = useTranslation();
  const texts = uiTexts(i18n.language);
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
          aria-label={visible ? texts.password.hide : texts.password.show}
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

/**
 * Language switch below the sign-in card, kept on this device: a button that opens a listbox of our own (the native
 * select drew its list in the browser's colors, white on white in dark mode). Keyboard: arrows, Home and End move,
 * Enter or Space chooses, Escape and Tab close. Without React Aria, like the rest of the sign-in chunk.
 */
export function LanguageSelect() {
  const { t } = useTranslation();
  const [choice, setChoice] = useState<Language | 'auto'>(storedLanguage() ?? 'auto');
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(0);
  // Keys can arrive faster than React renders (Home, then Enter), so the handlers read the active option from here.
  const activeIndex = useRef(0);
  const id = useId();
  const root = useRef<HTMLDivElement>(null);
  const button = useRef<HTMLButtonElement>(null);
  const options: { value: Language | 'auto'; label: string; lang?: string }[] = [
    { value: 'auto', label: t('menu.languageAuto') },
    ...languages.map((code) => ({ value: code, label: languageName(code), lang: code })),
  ];
  const current = options.find((option) => option.value === choice) ?? options[0];

  // Closes when the pointer goes down anywhere else; syncing with the document, not derived state.
  useEffect(() => {
    if (!open) {
      return;
    }
    const close = (event: PointerEvent) => {
      if (!root.current?.contains(event.target as Node)) {
        setOpen(false);
      }
    };
    document.addEventListener('pointerdown', close);
    return () => {
      document.removeEventListener('pointerdown', close);
    };
  }, [open]);

  function moveTo(index: number) {
    activeIndex.current = index;
    setActive(index);
  }

  function show() {
    moveTo(
      Math.max(
        0,
        options.findIndex((option) => option.value === choice),
      ),
    );
    setOpen(true);
  }

  function choose(value: Language | 'auto') {
    setChoice(value);
    setOpen(false);
    button.current?.focus();
    void setLanguage(value === 'auto' ? null : value);
  }

  return (
    <div ref={root} className={styles.language}>
      <button
        ref={button}
        type="button"
        className={styles.languageButton}
        aria-haspopup="listbox"
        aria-expanded={open}
        aria-controls={open ? `${id}-list` : undefined}
        aria-label={`${t('menu.language')}: ${current?.label ?? ''}`}
        onClick={() => {
          if (open) {
            setOpen(false);
          } else {
            show();
          }
        }}
        onKeyDown={(event) => {
          if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
            event.preventDefault();
            show();
          }
        }}
      >
        <IconLanguage size={16} stroke={1.75} aria-hidden />
        <span lang={current?.lang}>{current?.label}</span>
        <IconChevronDown size={14} stroke={2} aria-hidden />
      </button>
      {open && (
        <ul
          id={`${id}-list`}
          role="listbox"
          tabIndex={-1}
          className={styles.languageList}
          aria-label={t('menu.language')}
          aria-activedescendant={`${id}-${String(active)}`}
          ref={(list) => {
            list?.focus();
          }}
          onKeyDown={(event) => {
            const last = options.length - 1;
            const moves: Record<string, number> = {
              ArrowDown: Math.min(activeIndex.current + 1, last),
              ArrowUp: Math.max(activeIndex.current - 1, 0),
              Home: 0,
              End: last,
            };
            const target = moves[event.key];
            if (target !== undefined) {
              event.preventDefault();
              moveTo(target);
            } else if (event.key === 'Enter' || event.key === ' ') {
              event.preventDefault();
              const option = options[activeIndex.current];
              if (option !== undefined) {
                choose(option.value);
              }
            } else if (event.key === 'Escape') {
              event.preventDefault();
              setOpen(false);
              button.current?.focus();
            } else if (event.key === 'Tab') {
              setOpen(false);
            }
          }}
        >
          {options.map((option, index) => (
            <li
              key={option.value}
              id={`${id}-${String(index)}`}
              role="option"
              lang={option.lang}
              aria-selected={option.value === choice}
              data-active={index === active || undefined}
              className={styles.languageOption}
              onPointerEnter={() => {
                moveTo(index);
              }}
              onClick={() => {
                choose(option.value);
              }}
            >
              <span>{option.label}</span>
              {option.value === choice && <IconCheck size={14} stroke={2.25} aria-hidden />}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
