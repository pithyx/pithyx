import type { TFunction } from 'i18next';
import { useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import buttonStyles from '../ui/Button.module.css';
import { cx } from '../ui/cx';
import { IconAlertCircle, IconCopy } from '../ui/icons';
import { copyText } from '../platform/browser';
import { isFault, type DescribedError } from './describe';
import styles from './ErrorNotice.module.css';

/** The translated message of an error code; network failures have no code from the daemon. */
export function translateError(t: TFunction, error: DescribedError): string {
  return error.code === 'network'
    ? t('error.network')
    : // Error codes come from the generated union, so every one has a key (checked by i18n.test.ts).
      (t as unknown as (key: string, options: object) => string)(error.code, {
        ns: 'errors',
        ...error.params,
      });
}

export function useErrorMessage(error: DescribedError): string {
  const { t } = useTranslation();
  return translateError(t, error);
}

/** The request id with a copy button, so support can find the request in the logs. Plain elements only. */
export function RequestId({ requestId }: { requestId: string }) {
  const { t } = useTranslation();
  const value = useRef<HTMLElement>(null);
  const [manually, setManually] = useState(false);
  return (
    <div className={styles.request}>
      <span className={styles.requestLabel}>{t('error.requestId')}</span>
      <code ref={value} className={styles.requestValue}>
        {requestId}
      </code>
      <button
        type="button"
        data-native
        className={cx(
          buttonStyles.button,
          buttonStyles.quiet,
          buttonStyles.icon,
          buttonStyles.small,
        )}
        aria-label={t('error.copy')}
        title={t('error.copy')}
        onClick={() => {
          void copyText(requestId, value.current).then((done) => {
            setManually(!done);
          });
        }}
      >
        <IconCopy size={16} stroke={1.75} />
      </button>
      <span className={styles.requestHint} role="status">
        {manually ? t('error.copyManually') : ''}
      </span>
    </div>
  );
}

/**
 * An error message, used inline in forms. The request id appears only when the daemon failed; an expected refusal
 * such as a wrong password needs no support reference (the error dialog always shows it).
 */
export function ErrorNotice({ error, className }: { error: DescribedError; className?: string }) {
  const message = useErrorMessage(error);
  return (
    <div className={cx(styles.notice, className)} role="alert">
      <p className={styles.message}>
        <IconAlertCircle size={18} stroke={1.75} aria-hidden className={styles.icon} />
        {message}
      </p>
      {error.requestId !== '' && isFault(error) && <RequestId requestId={error.requestId} />}
    </div>
  );
}
