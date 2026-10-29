import type { PithyxApp } from '@pithyx/sdk';
import { Badge, Button } from '@pithyx/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { manifestText } from '../installed';
import { rootAppsKey, securityCall } from '../sdk/security';
import { describeProblem } from '../storage/shared';
import styles from './Settings.module.css';

/**
 * Settings > Security (D388): every app that holds root access to the box through a critical right, such as the
 * unfiltered container service, with one click to revoke it. Its background service restarts without the right.
 */
export function SecurityPage({ app }: { app: PithyxApp }) {
  const { t, i18n } = useTranslation('settings');
  const queryClient = useQueryClient();
  const apps = useQuery({
    queryKey: rootAppsKey,
    queryFn: () => securityCall(app, 'internal.admin.security.rootApps', {}),
  });
  const revoke = useMutation({
    mutationFn: (appId: string) =>
      securityCall(app, 'internal.admin.security.revokeRoot', { appId }),
    onSettled: () => queryClient.invalidateQueries({ queryKey: rootAppsKey }),
  });
  return (
    <div className={styles.content}>
      <header className={styles.pageHeader}>
        <div>
          <p className={styles.muted}>{t('security.intro')}</p>
        </div>
      </header>
      <section className={styles.group} aria-labelledby="security-root">
        <h3 id="security-root" className={styles.sectionTitle}>
          {t('security.root.title')}
        </h3>
        <p className={styles.muted}>{t('security.root.intro')}</p>
        {apps.isError && <ErrorNotice error={describeProblem(apps.error)} />}
        {apps.data?.length === 0 && <p className={styles.muted}>{t('security.root.none')}</p>}
        {apps.data?.map((entry) => {
          const names = entry.manifest as { name?: Record<string, string> };
          const rights = entry.elevated.filter(
            (right) => right.tier === 'critical' && right.granted,
          );
          return (
            <div key={entry.id} className={styles.card}>
              <div className={styles.pageHeader}>
                <div>
                  <strong>{manifestText(names.name ?? { en: entry.id }, i18n.language)}</strong>{' '}
                  <Badge tone="danger">{t('security.root.badge')}</Badge>
                  <span className={styles.detail}>
                    {t('security.root.rights', {
                      rights: rights.map((right) => right.right).join(', '),
                    })}
                  </span>
                </div>
                <Button
                  variant="danger"
                  isDisabled={revoke.isPending || entry.busy}
                  onPress={() => {
                    revoke.mutate(entry.id);
                  }}
                >
                  {t('security.root.revoke')}
                </Button>
              </div>
            </div>
          );
        })}
        {revoke.isError && <ErrorNotice error={describeProblem(revoke.error)} />}
      </section>
    </div>
  );
}
