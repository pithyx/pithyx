import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import type { DescribedError } from '../errors/describe';
import { translateError } from '../errors/ErrorNotice';

/** A client-side message, or a daemon error that belongs to a field. */
type FieldError = string | DescribedError;

/**
 * Field errors of an auth form. After a failed submit, focus moves to the first field with an error (in form order)
 * and all messages are announced through an assertive live region; a field's error clears as soon as it is edited.
 */
export function useFormErrors<Name extends string>(order: readonly Name[]) {
  const { t } = useTranslation();
  const [errors, setErrors] = useState<Partial<Record<Name, FieldError>>>({});
  const [announcement, setAnnouncement] = useState('');
  const inputs = useRef(new Map<Name, HTMLInputElement>());
  const text = (error: FieldError) =>
    typeof error === 'string' ? error : translateError(t, error);

  function show(next: Partial<Record<Name, FieldError>>, fallback?: Name) {
    setErrors(next);
    setAnnouncement(
      order
        .flatMap((name) => {
          const error = next[name];
          return error === undefined ? [] : [text(error)];
        })
        .join(' '),
    );
    const first = order.find((name) => next[name] !== undefined) ?? fallback;
    const input = first === undefined ? undefined : inputs.current.get(first);
    input?.focus();
    input?.select();
  }

  return {
    register: (name: Name) => {
      const error = errors[name];
      return {
        ref: (element: HTMLInputElement | null) => {
          if (element === null) {
            inputs.current.delete(name);
          } else {
            inputs.current.set(name, element);
          }
        },
        error: error === undefined ? undefined : text(error),
        onChange: () => {
          if (errors[name] !== undefined) {
            setErrors((current) => ({ ...current, [name]: undefined }));
          }
        },
      };
    },
    /** The client-side check before sending: true when nothing is wrong, otherwise the errors are shown. */
    check: (next: Partial<Record<Name, string>>): boolean => {
      if (Object.keys(next).length === 0) {
        setErrors({});
        setAnnouncement('');
        return true;
      }
      show(next);
      return false;
    },
    /** A failed request: errors for fields, and the field that gets focus when the error has none. */
    fail: (next: Partial<Record<Name, FieldError>>, fallback: Name) => {
      show(next, fallback);
    },
    announcement: (
      <p className="visually-hidden" aria-live="assertive">
        {announcement}
      </p>
    ),
  };
}
