import { IconAlertCircle, IconCopy } from '@tabler/icons-react';
import { useTranslation } from 'react-i18next';
import { IconButton } from '../ui/Button';
import { cx } from '../ui/cx';
import { copyText } from './copyText';
import type { DescribedError } from './describe';
import styles from './ErrorNotice.module.css';

/** The translated message of an error code; network failures have no code from the daemon. */
export function useErrorMessage(error: DescribedError): string {
  const { t } = useTranslation();
  return error.code === 'network'
    ? t('error.network')
    : // Error codes come from the generated union, so every one has a key (checked by i18n.test.ts).
      (t as unknown as (key: string, options: object) => string)(error.code, {
        ns: 'errors',
        ...error.params,
      });
}

/** The request id with a copy button, so support can find the request in the logs. */
export function RequestId({ requestId }: { requestId: string }) {
  const { t } = useTranslation();
  return (
    <div className={styles.request}>
      <span className={styles.requestLabel}>{t('error.requestId')}</span>
      <code className={styles.requestValue}>{requestId}</code>
      <IconButton label={t('error.copy')} size="sm" onPress={() => void copyText(requestId)}>
        <IconCopy size={16} stroke={1.75} />
      </IconButton>
    </div>
  );
}

/** An error message with the request id, used inline in forms. */
export function ErrorNotice({ error, className }: { error: DescribedError; className?: string }) {
  const message = useErrorMessage(error);
  return (
    <div className={cx(styles.notice, className)} role="alert">
      <p className={styles.message}>
        <IconAlertCircle size={18} stroke={1.75} aria-hidden className={styles.icon} />
        {message}
      </p>
      {error.requestId !== '' && <RequestId requestId={error.requestId} />}
    </div>
  );
}
