import { useId } from 'react';
import { useTranslation } from 'react-i18next';
import styles from '../auth/Auth.module.css';
import { Button } from '../auth/controls';
import { TrustSteps } from './TrustSteps';

/** The optional last step of the setup on a device that does not trust the box yet (D380). */
export function SetupTrustStep({ step, onContinue }: { step: string; onContinue: () => void }) {
  const { t } = useTranslation('security');
  const titleId = useId();
  return (
    <section className={styles.form} aria-labelledby={titleId}>
      <div className={styles.heading}>
        <p className={styles.step}>{step}</p>
        <h1 id={titleId} className={styles.title}>
          {t('trust.title')}
        </h1>
      </div>
      <TrustSteps />
      <Button variant="primary" className={styles.submit} onClick={onContinue}>
        {t('trust.continue')}
      </Button>
    </section>
  );
}
