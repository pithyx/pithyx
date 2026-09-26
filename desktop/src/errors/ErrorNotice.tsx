import { useTranslation } from 'react-i18next';
import { copyText } from './copyText';
import type { DescribedError } from './describe';

/** An error message with the request id and a copy button, used inline in forms and in the error dialog. */
export function ErrorNotice({ error }: { error: DescribedError }) {
  const { t } = useTranslation();
  const message =
    error.code === 'network'
      ? t('error.network')
      : // Error codes come from the generated union, so every one has a key (checked by i18n.test.ts).
        (t as unknown as (key: string, options: object) => string)(error.code, {
          ns: 'errors',
          ...error.params,
        });

  return (
    <div className="error-notice" role="alert">
      <p className="error-notice__message">{message}</p>
      {error.requestId !== '' && (
        <p className="error-notice__request">
          <span>{t('error.requestId')}</span> <code>{error.requestId}</code>{' '}
          <button
            type="button"
            className="button button--small"
            onClick={() => void copyText(error.requestId)}
          >
            {t('error.copy')}
          </button>
        </p>
      )}
    </div>
  );
}
