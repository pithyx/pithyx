import type { PithyxApp } from '@pithyx/sdk';
import { useQuery } from '@tanstack/react-query';
import { AttentionList, type AttentionItem } from '@pithyx/ui';
import { lazy, Suspense, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { formatBytes } from '../../desktop/format';
import { loadTranslations, useFormatLocale } from '../../i18n';
import { trustQuery } from '../../security/trust';
import { useMessages } from '../../shell/messages';
import { alertMessage } from '../../shell/storageAlerts';
import { LogoMark } from '../../ui/Logo';
import { coreAppIds } from '../ids';
import { useAppName } from '../names';
import { internalCall } from '../sdk/internal';
import { storeCall } from '../sdk/store';
import { systemCall } from '../sdk/system';
import { updatesCall } from '../sdk/updates';
import { useHealth } from '../storage/shared';
import styles from './Settings.module.css';
import type { SettingsPageId, SettingsPageInfo } from './pages';
import { uptime } from './uptime';

const TrustDialog = lazy(() =>
  Promise.all([import('../../security/TrustDialog'), loadTranslations('security')]).then(
    ([{ TrustDialog }]) => ({ default: TrustDialog }),
  ),
);

/**
 * The start page of Settings (D394): the box at a glance, **Needs attention** with one row per problem the box
 * already knows about and the action that leads to its fix, then every area of the settings as a tile.
 */
export function OverviewPage({
  app,
  pages,
  onOpen,
}: {
  app: PithyxApp;
  pages: readonly SettingsPageInfo[];
  onOpen: (page: SettingsPageId) => void;
}) {
  const { t } = useTranslation('settings');
  const message = useMessages('notifications');
  const appName = useAppName();
  const locale = useFormatLocale();
  const [trusting, setTrusting] = useState(false);
  const overview = useQuery({
    queryKey: ['settings', 'system', 'overview'],
    queryFn: () => systemCall(app, 'internal.admin.system.overview', {}),
  });
  const updates = useQuery({
    queryKey: ['settings', 'updates', 'status'],
    queryFn: () => updatesCall(app, 'internal.admin.updates.get', {}),
  });
  const store = useQuery({
    queryKey: ['settings', 'store', 'state'],
    queryFn: () => storeCall(app, 'internal.admin.store.state', {}),
  });
  const health = useHealth(app);
  const trust = useQuery(trustQuery);
  const box = overview.data;
  const available = updates.data?.available ?? null;
  const appUpdates = store.data?.updates ?? [];

  const attention: AttentionItem[] = [
    ...(health.data?.alerts ?? [])
      .filter((alert) => !alert.muted)
      .map((alert): AttentionItem => {
        const text = alertMessage(alert);
        return {
          id: `storage:${alert.key}`,
          tone: alert.severity === 'critical' ? 'danger' : 'warning',
          title: message(text, 'title'),
          description: message(text, 'body'),
          action: {
            label: t('overview.attention.openStorage'),
            onPress: () => {
              onOpen(alert.key.startsWith('disk.') ? 'disks' : 'pools');
            },
          },
        };
      }),
    ...(available === null
      ? []
      : [
          {
            id: 'update',
            tone: 'info',
            title: t('overview.attention.update.title', { version: available.version }),
            description: t('overview.attention.update.description'),
            action: {
              label: t('overview.attention.update.action'),
              onPress: () => {
                onOpen('updates');
              },
            },
          } satisfies AttentionItem,
        ]),
    ...(appUpdates.length === 0
      ? []
      : [
          {
            id: 'apps',
            tone: 'info',
            title: t('overview.attention.apps.title', { count: appUpdates.length }),
            description: appUpdates
              .map((update) => `${appName(update.id)} ${update.version}`)
              .join(', '),
            action: {
              label: t('overview.attention.apps.action'),
              onPress: () => {
                void internalCall(app, 'internal.admin.openApp', { app: coreAppIds.store });
              },
            },
          } satisfies AttentionItem,
        ]),
    ...(trust.data === 'untrusted'
      ? [
          {
            id: 'trust',
            tone: 'info',
            title: t('overview.attention.trust.title'),
            description: t('overview.attention.trust.description'),
            action: {
              label: t('overview.attention.trust.action'),
              onPress: () => {
                setTrusting(true);
              },
            },
          } satisfies AttentionItem,
        ]
      : []),
  ];

  return (
    <div className={styles.overviewPage}>
      {box !== undefined && (
        <section className={styles.boxCard} aria-label={t('overview.box.label')}>
          <LogoMark size={56} />
          <div className={styles.boxName}>
            <h2>{box.hostname}</h2>
            <p>
              {[
                t('overview.box.version', { version: box.version }),
                box.cpuModel,
                formatBytes(box.memoryTotalBytes, locale),
              ]
                .filter((part) => part !== null && part !== '')
                .join(' · ')}
            </p>
          </div>
          <dl className={styles.boxFacts}>
            <dt>{t('overview.box.runningFor')}</dt>
            <dd>{uptime(t, box.uptimeSeconds)}</dd>
            {updates.data !== undefined && (
              <>
                <dt>{t('overview.box.updates')}</dt>
                <dd>
                  {available === null
                    ? t('overview.box.upToDate')
                    : t('overview.box.updateReady', { version: available.version })}
                </dd>
              </>
            )}
          </dl>
        </section>
      )}
      <AttentionList items={attention} />
      <section aria-labelledby="settings-all">
        <h2 id="settings-all" className={styles.overviewHeading}>
          {t('overview.allSettings')}
        </h2>
        <ul className={styles.tiles}>
          {pages.map((entry) => {
            const Icon = entry.icon;
            return (
              <li key={entry.id}>
                <button
                  type="button"
                  className={styles.tile}
                  onClick={() => {
                    onOpen(entry.id);
                  }}
                >
                  <span className={styles.tileIcon} aria-hidden>
                    <Icon size={22} stroke={1.5} />
                  </span>
                  <span className={styles.tileText}>
                    <span className={styles.tileTitle}>{t(`pages.${entry.id}.title`)}</span>
                    <span className={styles.tileDescription}>
                      {t(`pages.${entry.id}.description`)}
                    </span>
                  </span>
                </button>
              </li>
            );
          })}
        </ul>
      </section>
      {trusting && (
        <Suspense fallback={null}>
          <TrustDialog
            onClose={() => {
              setTrusting(false);
            }}
          />
        </Suspense>
      )}
    </div>
  );
}
