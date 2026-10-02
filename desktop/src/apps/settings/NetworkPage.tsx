import type { PithyxApp } from '@pithyx/sdk';
import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { systemCall } from '../sdk/system';
import { describeProblem } from '../storage/shared';
import styles from './Settings.module.css';

/**
 * Network: every interface with its addresses, gateway and name servers, and whether it uses DHCP or a static
 * address. Read only: changes come with the 60-second confirmation timer of step 14, so a wrong address can never
 * lock the owner out; the page says so.
 */
export function NetworkPage({ app }: { app: PithyxApp }) {
  const { t } = useTranslation('settings');
  const network = useQuery({
    queryKey: ['settings', 'system', 'network'],
    queryFn: () => systemCall(app, 'internal.admin.system.network', {}),
  });
  const text = t as unknown as (key: string) => string;
  return (
    <div className={styles.content}>
      <header className={styles.pageHeader}>
        <div>
          <h2 className={styles.pageTitle}>{t('pages.network.title')}</h2>
          <p className={styles.muted}>{t('network.intro')}</p>
        </div>
      </header>
      <p className={styles.notice} role="note">
        <span className={styles.badge}>{t('network.changesLater')}</span>{' '}
        {t('network.changesExplain')}
      </p>
      {network.isError && <ErrorNotice error={describeProblem(network.error)} />}
      {network.data?.map((nic) => (
        <section key={nic.name} className={styles.card} aria-labelledby={`nic-${nic.name}`}>
          <h3 id={`nic-${nic.name}`} className={styles.sectionTitle}>
            {nic.name}{' '}
            <span className={styles.muted}>{nic.up ? t('network.up') : t('network.down')}</span>
          </h3>
          <dl className={styles.facts}>
            <div className={styles.fact}>
              <dt>{t('network.method')}</dt>
              <dd>{text(`network.methods.${nic.method}`)}</dd>
            </div>
            <div className={styles.fact}>
              <dt>{t('network.addresses')}</dt>
              <dd className={styles.mono}>{nic.addresses.join(', ') || t('network.none')}</dd>
            </div>
            <div className={styles.fact}>
              <dt>{t('network.gateway')}</dt>
              <dd className={styles.mono}>{nic.gateways.join(', ') || t('network.none')}</dd>
            </div>
            <div className={styles.fact}>
              <dt>{t('network.dns')}</dt>
              <dd className={styles.mono}>{nic.dnsServers.join(', ') || t('network.none')}</dd>
            </div>
            {nic.macAddress !== null && (
              <div className={styles.fact}>
                <dt>{t('network.mac')}</dt>
                <dd className={styles.mono}>{nic.macAddress}</dd>
              </div>
            )}
          </dl>
        </section>
      ))}
    </div>
  );
}
