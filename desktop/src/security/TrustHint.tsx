import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Button } from '@pithyx/ui';
import styles from './Security.module.css';
import { dismissTrustHint } from './trust';

/**
 * Shown after sign-in on a device that does not trust the box (D380), until the user hides it on this device. The
 * desktop loads it only for such a device.
 */
export function TrustHint({ onTrust }: { onTrust: () => void }) {
  const { t } = useTranslation('security');
  const [hidden, setHidden] = useState(false);
  if (hidden) {
    return null;
  }
  return (
    <aside className={styles.hint} aria-label={t('trust.title')}>
      <p>{t('trust.hint')}</p>
      <Button variant="primary" size="sm" onPress={onTrust}>
        {t('trust.hintAction')}
      </Button>
      <Button
        variant="quiet"
        size="sm"
        onPress={() => {
          dismissTrustHint();
          setHidden(true);
        }}
      >
        {t('trust.hintDismiss')}
      </Button>
    </aside>
  );
}
