import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { buttonStyles, cx } from '@pithyx/ui/native';
import { deviceTrust } from '../platform/browser';
import styles from './Security.module.css';
import { rootCertificateUrl, trustQuery } from './trust';

const DEVICES = ['windows', 'macos', 'ios', 'android', 'linux', 'firefox'] as const;

/**
 * How to make this device trust the box (D380): whether it does, the root to download, the steps per system and a
 * check after the browser restarted. Plain elements only, so the setup screen can show it before the desktop's
 * components load.
 */
export function TrustSteps({ facts }: { facts?: ReactNode }) {
  const { t } = useTranslation('security');
  const queryClient = useQueryClient();
  const trust = useQuery(trustQuery);
  const recheck = useMutation({
    mutationFn: () => deviceTrust(true),
    onSuccess: (value) => {
      queryClient.setQueryData(trustQuery.queryKey, value);
    },
  });
  return (
    <div className={styles.steps}>
      {trust.data !== undefined && (
        <p className={cx(styles.status, trust.data === 'trusted' && styles.trusted)} role="status">
          {t(`trust.${trust.data}`)}
        </p>
      )}
      <p className={styles.muted}>{t('trust.intro')}</p>
      <div className={styles.buttons}>
        <a
          className={cx(buttonStyles.button, buttonStyles.primary)}
          href={rootCertificateUrl}
          download
        >
          {t('trust.download')}
        </a>
        {trust.data === 'untrusted' && (
          <button
            type="button"
            className={buttonStyles.button}
            disabled={recheck.isPending}
            onClick={() => {
              recheck.mutate();
            }}
          >
            {recheck.isPending ? t('trust.checking') : t('trust.check')}
          </button>
        )}
      </div>
      {facts}
      <p className={styles.muted}>{t('trust.devicesTitle')}</p>
      <ul className={styles.devices}>
        {DEVICES.map((device) => (
          <li key={device}>
            <strong>{t(`trust.devices.${device}.name`)}</strong>{' '}
            {t(`trust.devices.${device}.steps`)}
          </li>
        ))}
      </ul>
      <p className={styles.muted}>{t('trust.restart')}</p>
    </div>
  );
}
