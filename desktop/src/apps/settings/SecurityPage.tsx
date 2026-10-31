import type { PithyxApp } from '@pithyx/sdk';
import { Button, FormRow, FormSection, StatusBadge } from '@pithyx/ui';
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
      <p className={styles.muted}>{t('security.intro')}</p>
      <FormSection title={t('security.root.title')}>
        <p className={styles.sectionIntro}>{t('security.root.intro')}</p>
        {apps.isError && <ErrorNotice error={describeProblem(apps.error)} />}
        {apps.data?.length === 0 && (
          <p className={styles.sectionIntro}>{t('security.root.none')}</p>
        )}
        {apps.data?.map((entry) => {
          const names = entry.manifest as { name?: Record<string, string> };
          const rights = entry.elevated.filter(
            (right) => right.tier === 'critical' && right.granted,
          );
          return (
            <FormRow
              key={entry.id}
              label={
                <>
                  {manifestText(names.name ?? { en: entry.id }, i18n.language)}{' '}
                  <StatusBadge tone="danger">{t('security.root.badge')}</StatusBadge>
                </>
              }
              hint={t('security.root.rights', {
                rights: rights.map((right) => right.right).join(', '),
              })}
            >
              <Button
                variant="danger"
                isDisabled={revoke.isPending || entry.busy}
                onPress={() => {
                  revoke.mutate(entry.id);
                }}
              >
                {t('security.root.revoke')}
              </Button>
            </FormRow>
          );
        })}
        {revoke.isError && <ErrorNotice error={describeProblem(revoke.error)} />}
      </FormSection>
    </div>
  );
}
