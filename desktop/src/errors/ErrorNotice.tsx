import type { TFunction } from 'i18next';
import { useEffect } from 'react';
import { useTranslation } from 'react-i18next';
import { cx } from '@pithyx/ui';
import { IconAlertCircle } from '../ui/icons';
import type { DescribedError } from './describe';
import styles from './ErrorNotice.module.css';

/**
 * The translated message of an error code; network failures have no code from the daemon. The daemon's and the SDK's
 * codes all have a key (checked by i18n.test.ts); an app's own code (`app.ui.showError`) has none and shows the
 * problem's `detail` instead.
 */
export function translateError(t: TFunction, error: DescribedError): string {
  return error.code === 'network'
    ? t('error.network')
    : (t as unknown as (key: string, options: object) => string)(error.code, {
        ns: 'errors',
        ...error.params,
        ...(error.detail === undefined || error.detail === ''
          ? {}
          : { defaultValue: error.detail }),
      });
}

export function useErrorMessage(error: DescribedError): string {
  const { t } = useTranslation();
  return translateError(t, error);
}

/**
 * Writes the request id of an error to the browser console once it is shown. The UI never shows it (CLAUDE.md, Tracing
 * and logging); the console and the daemon's logs keep it for support.
 */
export function useLogRequestId(error: DescribedError | null): void {
  const requestId = error?.requestId ?? '';
  const code = error?.code ?? '';
  useEffect(() => {
    if (requestId !== '') {
      console.error(`Pithyx error ${code}, request id ${requestId}`);
    }
  }, [code, requestId]);
}

/** An error message, used inline in forms. Its request id goes to the console only. */
export function ErrorNotice({ error, className }: { error: DescribedError; className?: string }) {
  const message = useErrorMessage(error);
  useLogRequestId(error);
  return (
    <div className={cx(styles.notice, className)} role="alert">
      <p className={styles.message}>
        <IconAlertCircle size={18} stroke={1.75} aria-hidden className={styles.icon} />
        {message}
      </p>
    </div>
  );
}
