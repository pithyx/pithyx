import type { PithyxApp } from '@pithyx/sdk';
import { useQuery } from '@tanstack/react-query';
import { Button, FormRow, FormSection } from '@pithyx/ui';
import { useTranslation } from 'react-i18next';
import { formatBytes } from '../../desktop/format';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { useFormatLocale } from '../../i18n';
import { coreAppIds } from '../ids';
import { internalCall } from '../sdk/internal';
import { systemCall } from '../sdk/system';
import { describeProblem } from '../storage/shared';
import styles from './Settings.module.css';
import { uptime } from './uptime';

/**
 * Info Center: what runs on this box. Pithyx version and commit, system, machine, processor, memory, uptime; and the
 * way to the support bundle in the Log Center (D412).
 */
export function InfoCenterPage({ app }: { app: PithyxApp }) {
  const { t } = useTranslation('settings');
  const locale = useFormatLocale();
  const overview = useQuery({
    queryKey: ['settings', 'system', 'overview'],
    queryFn: () => systemCall(app, 'internal.admin.system.overview', {}),
  });
  const data = overview.data;
  const rows: [string, string][] =
    data === undefined
      ? []
      : [
          [
            t('info.version'),
            data.commit === null ? data.version : `${data.version} (${data.commit.slice(0, 7)})`,
          ],
          [t('info.hostname'), data.hostname],
          [t('info.os'), data.operatingSystem],
          [t('info.kernel'), data.kernel],
          [t('info.machine'), data.machine ?? t('info.unknown')],
          [t('info.cpu'), data.cpuModel ?? t('info.unknown')],
          [t('info.cores'), new Intl.NumberFormat(locale).format(data.cpuCores)],
          [t('info.memory'), formatBytes(data.memoryTotalBytes, locale)],
          [t('info.uptime'), uptime(t, data.uptimeSeconds)],
        ];
  return (
    <div className={styles.content}>
      {overview.isError && <ErrorNotice error={describeProblem(overview.error)} />}
      {data !== undefined && (
        <FormSection title={t('info.section')} note={t('info.intro')}>
          {rows.map(([label, value]) => (
            <FormRow key={label} label={label}>
              {value}
            </FormRow>
          ))}
        </FormSection>
      )}
      <FormSection title={t('info.support.section')} note={t('info.support.intro')}>
        <FormRow label={t('info.support.label')} hint={t('info.support.hint')}>
          <Button
            onPress={() => {
              void internalCall(app, 'internal.admin.openApp', {
                app: coreAppIds.logs,
                page: 'support',
              });
            }}
          >
            {t('info.support.open')}
          </Button>
        </FormRow>
      </FormSection>
    </div>
  );
}
