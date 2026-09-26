import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { copyText } from '../errors/copyText';

/** Shows the recovery key once. Continuing is possible only after confirming it was stored. */
export function RecoveryKeyCard({
  recoveryKey,
  onContinue,
}: {
  recoveryKey: string;
  onContinue: () => void;
}) {
  const { t } = useTranslation('auth');
  const [stored, setStored] = useState(false);
  const confirmId = useId();

  return (
    <section className="card recovery-key" aria-labelledby="recovery-key-title">
      <h1 id="recovery-key-title" className="card__title">
        {t('recoveryKey.title')}
      </h1>
      <p>{t('recoveryKey.intro')}</p>
      <p className="recovery-key__value">
        <code data-testid="recovery-key">{recoveryKey}</code>
      </p>
      <div className="card__row no-print">
        <button type="button" className="button" onClick={() => void copyText(recoveryKey)}>
          {t('recoveryKey.copy')}
        </button>
        <button
          type="button"
          className="button"
          onClick={() => {
            window.print();
          }}
        >
          {t('recoveryKey.print')}
        </button>
      </div>
      <p className="field field--inline no-print">
        <input
          id={confirmId}
          type="checkbox"
          checked={stored}
          onChange={(event) => {
            setStored(event.target.checked);
          }}
        />
        <label htmlFor={confirmId}>{t('recoveryKey.confirm')}</label>
      </p>
      <button
        type="button"
        className="button button--primary no-print"
        disabled={!stored}
        onClick={onContinue}
      >
        {t('recoveryKey.continue')}
      </button>
    </section>
  );
}
