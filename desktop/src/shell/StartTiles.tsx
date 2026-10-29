import { IconActivity, IconDatabase, IconFolder, IconHome, IconRefresh } from '../ui/icons';
import { unwrap, type components } from '@pithyx/api-client';
import { useQuery } from '@tanstack/react-query';
import { useEffect } from 'react';
import { Button as AriaButton } from 'react-aria-components';
import { useTranslation } from 'react-i18next';
import { StatusBadge } from '@pithyx/ui';
import { api } from '../api/client';
import { formatBytes } from '../desktop/format';
import { realtime, useRealtime } from '../realtime/connection';
import styles from './StartMenu.module.css';

type Share = components['schemas']['ShareInfo'];
type Pool = components['schemas']['Pool'];
type ResourceSample = components['schemas']['ResourceSample'];
type UpdateStatus = components['schemas']['UpdateStatus'];

const NO_POOLS: Pool[] = [];

function poolTone(pool: Pool): 'success' | 'warning' | 'danger' {
  if (pool.state !== 'ONLINE') {
    return pool.state === 'DEGRADED' ? 'warning' : 'danger';
  }
  return pool.capacityPercent >= 80 ? 'warning' : 'success';
}

/**
 * A quantity drawn as a bar inside a tile. The tile is a button, whose content screen readers read as plain text, so
 * the bar is decorative and the tile's text carries the facts.
 */
function TileBar({ value, tone }: { value: number; tone: 'success' | 'warning' | 'danger' }) {
  return (
    <span className={styles.tileBar} aria-hidden>
      <span data-tone={tone} style={{ width: `${String(Math.min(100, Math.max(0, value)))}%` }} />
    </span>
  );
}

/** The status tiles of an administrator: every pool, the system and the updates. */
export function StatusTiles({
  pools,
  sample,
  updates,
  locale,
  onOpen,
}: {
  pools: readonly Pool[];
  sample: ResourceSample | null | undefined;
  updates: UpdateStatus | undefined;
  locale: string;
  onOpen: (app: 'storage' | 'monitor' | 'settings') => void;
}) {
  const { t } = useTranslation();
  const percent = new Intl.NumberFormat(locale, { style: 'percent', maximumFractionDigits: 0 });

  return (
    <section className={styles.tileSection} aria-labelledby="start-status">
      <div className={styles.tileHeader}>
        <h3 id="start-status" className={styles.sectionTitle}>
          {t('start.status')}
        </h3>
        <AriaButton
          className={styles.link}
          onPress={() => {
            onOpen('storage');
          }}
        >
          {t('start.openStorage')}
        </AriaButton>
      </div>
      <div className={styles.tileGrid}>
        {pools.map((pool) => {
          const tone = poolTone(pool);
          return (
            <AriaButton
              key={pool.name}
              className={styles.statusTile}
              onPress={() => {
                onOpen('storage');
              }}
            >
              <span className={styles.tileTop}>
                <IconDatabase className={styles.tileIcon} size={20} stroke={1.75} aria-hidden />
                <StatusBadge tone={tone}>
                  {pool.state === 'ONLINE' && tone === 'warning'
                    ? percent.format(pool.capacityPercent / 100)
                    : t('start.poolState', { state: pool.state })}
                </StatusBadge>
              </span>
              <b>{pool.name}</b>
              <small>{t('start.poolFacts', { free: formatBytes(pool.freeBytes, locale) })}</small>
              <TileBar value={pool.capacityPercent} tone={tone} />
            </AriaButton>
          );
        })}
        {sample !== undefined && sample !== null && (
          <AriaButton
            className={styles.statusTile}
            onPress={() => {
              onOpen('monitor');
            }}
          >
            <IconActivity className={styles.tileIcon} size={20} stroke={1.75} aria-hidden />
            <b>{t('start.systemTile')}</b>
            <small>
              {t('start.systemFacts', {
                cpu: percent.format(sample.cpu.percent / 100),
                used: formatBytes(sample.memory.totalBytes - sample.memory.availableBytes, locale),
                total: formatBytes(sample.memory.totalBytes, locale),
              })}
            </small>
            <TileBar value={sample.cpu.percent} tone="success" />
          </AriaButton>
        )}
        {updates !== undefined && (
          <AriaButton
            className={styles.statusTile}
            onPress={() => {
              onOpen('settings');
            }}
          >
            <IconRefresh className={styles.tileIcon} size={20} stroke={1.75} aria-hidden />
            <b>{t('start.updates')}</b>
            <small>
              {updates.available === null
                ? t('start.updatesCurrent', { version: updates.installedVersion })
                : t('start.updatesAvailable', { version: updates.available.version })}
            </small>
          </AriaButton>
        )}
      </div>
    </section>
  );
}

/** The user's home and shared folders with the access they have, each opening in Files. */
export function FolderTiles({
  shares,
  onFolder,
}: {
  shares: readonly Share[];
  onFolder: (share: number) => void;
}) {
  const { t } = useTranslation();
  const folders = shares
    .filter((share: Share) => share.kind !== 'userHome' && share.unavailable === null)
    .toSorted((a, b) => Number(b.kind === 'home') - Number(a.kind === 'home'));
  if (folders.length === 0) {
    return null;
  }
  return (
    <section className={styles.tileSection} aria-labelledby="start-folders">
      <h3 id="start-folders" className={styles.sectionTitle}>
        {t('start.folders')}
      </h3>
      <ul className={styles.folderList}>
        {folders.map((share) => (
          <li key={share.id}>
            <AriaButton
              className={styles.folder}
              onPress={() => {
                onFolder(share.id);
              }}
            >
              {share.kind === 'home' ? (
                <IconHome className={styles.tileIcon} size={18} stroke={1.75} aria-hidden />
              ) : (
                <IconFolder className={styles.tileIcon} size={18} stroke={1.75} aria-hidden />
              )}
              <span className={styles.folderName}>
                {share.kind === 'home' ? t('appHost.files.home') : share.name}
              </span>
              <span className={styles.folderAccess}>
                {share.kind === 'home' ? t('start.onlyYou') : t(`start.access.${share.access}`)}
              </span>
            </AriaButton>
          </li>
        ))}
      </ul>
    </section>
  );
}

/**
 * The tile area of the Pithyx menu (D394): status tiles for administrators, then the user's folders with their
 * access. "For you" tiles and recent files wait for their decision (#329) and an API.
 */
export function StartTiles({
  isAdmin,
  locale,
  onOpen,
  onFolder,
}: {
  isAdmin: boolean;
  locale: string;
  onOpen: (app: 'storage' | 'monitor' | 'settings') => void;
  onFolder: (share: number) => void;
}) {
  const shares = useQuery({
    queryKey: ['start', 'shares'],
    queryFn: async () => unwrap(await api.GET('/api/shares')),
  });
  return (
    <>
      {isAdmin && <LiveStatusTiles locale={locale} onOpen={onOpen} />}
      <FolderTiles shares={shares.data ?? []} onFolder={onFolder} />
    </>
  );
}

/** The status tiles on the live topics; only administrators subscribe. */
function LiveStatusTiles({
  locale,
  onOpen,
}: {
  locale: string;
  onOpen: (app: 'storage' | 'monitor' | 'settings') => void;
}) {
  // Subscribing is synchronization with the WebSocket, not data fetching.
  useEffect(() => {
    const stops = (['storage/pools', 'system/resources', 'system/updates'] as const).map((topic) =>
      realtime.subscribe(topic),
    );
    return () => {
      for (const stop of stops) {
        stop();
      }
    };
  }, []);
  const pools = useRealtime((state) => state.data['storage/pools']?.pools ?? NO_POOLS);
  const sample = useRealtime((state) => state.data['system/resources']?.latest);
  const updates = useRealtime((state) => state.data['system/updates']);
  return (
    <StatusTiles pools={pools} sample={sample} updates={updates} locale={locale} onOpen={onOpen} />
  );
}
