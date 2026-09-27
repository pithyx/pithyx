import { IconCheck, IconCopy, IconKey, IconPrinter } from '../ui/icons';
import { useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { copyText } from '../errors/copyText';
import { cx } from '../ui/cx';
import styles from './Auth.module.css';
import { Button, Checkbox } from './controls';
import { RecoveryKeyValue } from './parts';

/** Shows a recovery key once. Continuing is possible only after confirming it was stored. */
export function RecoveryKeyCard({
  recoveryKey,
  step,
  title,
  intro,
  continueLabel,
  onContinue,
}: {
  recoveryKey: string;
  step?: string;
  title: string;
  intro: string;
  continueLabel: string;
  onContinue: () => void;
}) {
  const { t } = useTranslation('auth');
  const titleId = useId();
  const [stored, setStored] = useState(false);
  const [copied, setCopied] = useState(false);

  return (
    <section className={styles.form} aria-labelledby={titleId}>
      <div className={styles.heading}>
        {step !== undefined && <p className={styles.step}>{step}</p>}
        <h1 id={titleId} className={styles.title}>
          {title}
        </h1>
        <p className={styles.intro}>{intro}</p>
      </div>
      <div className={styles.key}>
        <IconKey size={20} stroke={1.75} aria-hidden className={styles.keyIcon} />
        <RecoveryKeyValue value={recoveryKey} />
      </div>
      <div className={cx(styles.row, styles.noPrint)}>
        <Button
          autoFocus
          onClick={() => {
            void copyText(recoveryKey).then(() => {
              setCopied(true);
            });
          }}
        >
          {copied ? (
            <IconCheck size={18} stroke={1.75} aria-hidden />
          ) : (
            <IconCopy size={18} stroke={1.75} aria-hidden />
          )}
          {copied ? t('recoveryKey.copied') : t('recoveryKey.copy')}
        </Button>
        <Button
          onClick={() => {
            window.print();
          }}
        >
          <IconPrinter size={18} stroke={1.75} aria-hidden />
          {t('recoveryKey.print')}
        </Button>
      </div>
      <div className={styles.noPrint}>
        <Checkbox checked={stored} onChange={setStored}>
          {t('recoveryKey.confirm')}
        </Checkbox>
      </div>
      <Button
        variant="primary"
        disabled={!stored}
        className={cx(styles.submit, styles.noPrint)}
        onClick={onContinue}
      >
        {continueLabel}
      </Button>
    </section>
  );
}
