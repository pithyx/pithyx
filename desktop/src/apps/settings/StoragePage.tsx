import type { ErrorCode } from '@pithyx/api-client';
import type { PithyxApp } from '@pithyx/sdk';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { formatBytes } from '../../desktop/format';
import type { DescribedError } from '../../errors/describe';
import { ErrorNotice } from '../../errors/ErrorNotice';
import { Button } from '../../ui/Button';
import { Badge } from '../../ui/Controls';
import { IconPlus } from '../../ui/icons';
import {
  internalCall,
  onInternal,
  STORAGE_CHANGED,
  type Dataset,
  type Pool,
  type StorageDisk,
  type Vdev,
} from '../sdk/internal';
import { PoolActionDialog, type PoolAction } from './PoolActionDialog';
import { PoolWizard } from './PoolWizard';
import { SnapshotsSection } from './SnapshotsSection';
import styles from './Settings.module.css';

const storageKey = ['settings', 'storage'] as const;

/** A failed internal call as the error notice shows it. */
export function describeProblem(error: unknown): DescribedError {
  const problem = error as { code?: string; params?: Record<string, unknown>; requestId?: string };
  return {
    code: (problem.code ?? 'system.internal.unexpected') as ErrorCode,
    params: problem.params ?? {},
    requestId: problem.requestId ?? '',
  };
}

const HEALTHY_STATES = new Set(['ONLINE']);

export function stateTone(state: string): 'accent' | 'highlight' | 'danger' | 'neutral' {
  if (HEALTHY_STATES.has(state)) {
    return 'accent';
  }
  return state === 'DEGRADED' ? 'highlight' : state === 'UNKNOWN' ? 'neutral' : 'danger';
}

/** Pools with their layout and health, and every disk with the guard's verdict. Live through the storage topics. */
export function StoragePage({ app }: { app: PithyxApp }) {
  const { t } = useTranslation('settings');
  const queryClient = useQueryClient();
  const [creating, setCreating] = useState(false);
  const [action, setAction] = useState<{ pool: Pool; vdev?: Vdev; action: PoolAction } | null>(
    null,
  );
  const pools = useQuery({
    queryKey: [...storageKey, 'pools'],
    queryFn: () => internalCall(app, 'internal.admin.storage.pools', {}),
  });
  const disks = useQuery({
    queryKey: [...storageKey, 'disks'],
    queryFn: () => internalCall(app, 'internal.admin.storage.disks', {}),
  });

  // The desktop forwards changes of the storage topics; this syncs the cached answers with them.
  useEffect(
    () =>
      onInternal(app, STORAGE_CHANGED, () => {
        void queryClient.invalidateQueries({ queryKey: storageKey });
      }),
    [app, queryClient],
  );

  return (
    <div className={styles.storage}>
      <header className={styles.pageHeader}>
        <div>
          <h2 className={styles.pageTitle}>{t('nav.storage')}</h2>
          <p className={styles.muted}>{t('storage.intro')}</p>
        </div>
        <Button
          variant="primary"
          onPress={() => {
            setCreating(true);
          }}
          isDisabled={disks.data === undefined}
        >
          <IconPlus size={16} aria-hidden />
          {t('storage.create.button')}
        </Button>
      </header>
      {disks.data?.environment === 'dev' && <p className={styles.notice}>{t('storage.dev')}</p>}

      <section aria-labelledby="storage-pools" className={styles.section}>
        <h3 id="storage-pools" className={styles.sectionTitle}>
          {t('storage.pools.title')}
        </h3>
        {pools.isError && <ErrorNotice error={describeProblem(pools.error)} />}
        {pools.data?.pools.length === 0 && (
          <p className={styles.muted}>{t('storage.pools.empty')}</p>
        )}
        <div className={styles.pools}>
          {pools.data?.pools.map((pool) => (
            <PoolCard
              key={pool.name}
              pool={pool}
              datasets={pools.data.datasets.filter((dataset) => dataset.pool === pool.name)}
              onAction={(poolAction, vdev) => {
                setAction({ pool, action: poolAction, ...(vdev === undefined ? {} : { vdev }) });
              }}
            />
          ))}
        </div>
      </section>

      {pools.data !== undefined && <SnapshotsSection app={app} datasets={pools.data.datasets} />}

      <section aria-labelledby="storage-disks" className={styles.section}>
        <h3 id="storage-disks" className={styles.sectionTitle}>
          {t('storage.disks.title')}
        </h3>
        {disks.isError && <ErrorNotice error={describeProblem(disks.error)} />}
        {disks.data !== undefined && <DiskTable disks={disks.data.disks} />}
      </section>

      {creating && disks.data !== undefined && (
        <PoolWizard
          app={app}
          disks={disks.data.disks}
          onClose={() => {
            setCreating(false);
          }}
        />
      )}
      {action !== null && disks.data !== undefined && (
        <PoolActionDialog
          app={app}
          pool={action.pool}
          {...(action.vdev === undefined ? {} : { vdev: action.vdev })}
          action={action.action}
          disks={disks.data.disks}
          onClose={() => {
            setAction(null);
          }}
        />
      )}
    </div>
  );
}

type OnAction = (action: PoolAction, vdev?: Vdev) => void;

function PoolCard({
  pool,
  datasets,
  onAction,
}: {
  pool: Pool;
  datasets: Dataset[];
  onAction: OnAction;
}) {
  const { t, i18n } = useTranslation('settings');
  const data = pool.vdevs.filter((vdev) => vdev.role === 'data');
  const others = pool.vdevs.filter((vdev) => vdev.role !== 'data');
  return (
    <article className={styles.pool} aria-labelledby={`pool-${pool.name}`}>
      <div className={styles.poolHead}>
        <h4 id={`pool-${pool.name}`} className={styles.poolName}>
          {pool.name}
        </h4>
        <Badge tone={stateTone(pool.state)}>{t(`storage.state.${stateKey(pool.state)}`)}</Badge>
      </div>
      <div className={styles.usage}>
        <span>
          {t('storage.pools.used', {
            used: formatBytes(pool.allocatedBytes, i18n.language),
            size: formatBytes(pool.sizeBytes, i18n.language),
          })}
        </span>
        <span className={styles.bar} aria-hidden>
          <span className={styles.fill} style={{ width: `${String(pool.capacityPercent)}%` }} />
        </span>
      </div>
      <p className={styles.muted}>
        {t('storage.pools.layout', { layout: data.map((vdev) => vdevLabel(t, vdev)).join(' + ') })}
      </p>
      {pool.scan !== null && <p className={styles.muted}>{scanText(t, pool, i18n.language)}</p>}
      {pool.state !== 'ONLINE' && (
        <p className={styles.notice}>{t('storage.pools.degradedHint')}</p>
      )}
      <ul className={styles.vdevs} aria-label={t('storage.pools.devices', { pool: pool.name })}>
        {[...data, ...others].map((vdev) => (
          <VdevItem key={vdev.name} vdev={vdev} onAction={onAction} />
        ))}
      </ul>
      <details className={styles.datasets}>
        <summary>{t('storage.datasets.title', { count: datasets.length })}</summary>
        <ul>
          {datasets.map((dataset) => (
            <li key={dataset.name}>
              <span className={styles.mono}>{dataset.name}</span>
              <span className={styles.muted}>
                {' '}
                {dataset.mountPoint ?? t('storage.datasets.notMounted')} ·{' '}
                {formatBytes(dataset.usedBytes, i18n.language)}
              </span>
            </li>
          ))}
        </ul>
      </details>
      <div className={styles.poolActions}>
        <Button
          size="sm"
          onPress={() => {
            onAction('dataset.create');
          }}
        >
          {t('storage.actions.datasetCreate.button')}
        </Button>
        <Button
          size="sm"
          onPress={() => {
            onAction('pool.spare.add');
          }}
        >
          {t('storage.actions.spareAdd.button')}
        </Button>
        <Button
          size="sm"
          variant="danger"
          onPress={() => {
            onAction('pool.destroy');
          }}
        >
          {t('storage.actions.destroy.button')}
        </Button>
      </div>
    </article>
  );
}

function VdevItem({ vdev, onAction }: { vdev: Vdev; onAction: OnAction }) {
  const { t } = useTranslation('settings');
  const leaves = vdev.children.length === 0 ? [vdev] : vdev.children;
  return (
    <li className={styles.vdev}>
      <span className={styles.vdevType}>
        {vdev.role === 'data' ? vdevLabel(t, vdev) : t(`storage.role.${roleKey(vdev.role)}`)}
      </span>
      <ul className={styles.leaves}>
        {leaves.map((leaf) => (
          <li key={leaf.name} className={styles.leaf} data-state={leaf.state}>
            <span className={styles.mono}>{leaf.name}</span>
            <Badge tone={stateTone(leaf.state)}>{t(`storage.state.${stateKey(leaf.state)}`)}</Badge>
            <span className={styles.leafActions}>
              {vdev.role === 'spare' ? (
                <Button
                  size="sm"
                  variant="quiet"
                  onPress={() => {
                    onAction('pool.spare.remove', leaf);
                  }}
                >
                  {t('storage.actions.spareRemove.button')}
                </Button>
              ) : (
                <>
                  <Button
                    size="sm"
                    variant="quiet"
                    onPress={() => {
                      onAction(leaf.state === 'OFFLINE' ? 'pool.online' : 'pool.offline', leaf);
                    }}
                  >
                    {t(
                      leaf.state === 'OFFLINE'
                        ? 'storage.actions.online.button'
                        : 'storage.actions.offline.button',
                    )}
                  </Button>
                  <Button
                    size="sm"
                    variant="quiet"
                    onPress={() => {
                      onAction('pool.replace', leaf);
                    }}
                  >
                    {t('storage.actions.replace.button')}
                  </Button>
                </>
              )}
            </span>
            {leaf.readErrors + leaf.writeErrors + leaf.checksumErrors > 0 && (
              <span className={styles.errors}>
                {t('storage.pools.errors', {
                  read: leaf.readErrors,
                  write: leaf.writeErrors,
                  checksum: leaf.checksumErrors,
                })}
              </span>
            )}
          </li>
        ))}
      </ul>
    </li>
  );
}

const STATES = [
  'online',
  'degraded',
  'faulted',
  'offline',
  'unavail',
  'removed',
  'suspended',
] as const;
type StateKey = (typeof STATES)[number] | 'unknown';

function stateKey(state: string): StateKey {
  const key = state.toLowerCase();
  return STATES.find((known) => known === key) ?? 'unknown';
}

const ROLES = ['log', 'cache', 'special', 'dedup', 'spare'] as const;

function roleKey(role: string): (typeof ROLES)[number] {
  return ROLES.find((known) => known === role) ?? 'special';
}

type Translate = ReturnType<typeof useTranslation<'settings'>>['t'];

function vdevLabel(t: Translate, vdev: Vdev): string {
  switch (vdev.type) {
    case 'mirror':
      return t('storage.vdev.mirror', { count: vdev.children.length });
    case 'raidz':
      return t('storage.vdev.raidz', {
        parity: vdev.parityDisks ?? 1,
        count: vdev.children.length,
      });
    case 'disk':
    case 'file':
      return t('storage.vdev.single');
    default:
      return vdev.type;
  }
}

function scanText(t: Translate, pool: Pool, locale: string): string {
  const scan = pool.scan;
  if (scan === null) {
    return '';
  }
  const date =
    scan.endedAt === null
      ? ''
      : new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeStyle: 'short' }).format(
          new Date(scan.endedAt),
        );
  const percent =
    scan.toExamineBytes === 0 ? 0 : Math.floor((scan.examinedBytes / scan.toExamineBytes) * 100);
  const kind = scan.kind === 'resilver' ? 'resilver' : 'scrub';
  if (scan.state === 'scanning') {
    return scan.paused
      ? t('storage.scan.paused', { percent })
      : t(`storage.scan.${kind}Running`, { percent });
  }
  return scan.state === 'finished'
    ? t(`storage.scan.${kind}Finished`, { date, errors: scan.errors })
    : t('storage.scan.canceled');
}

function DiskTable({ disks }: { disks: StorageDisk[] }) {
  const { t, i18n } = useTranslation('settings');
  return (
    <table className={styles.table}>
      <caption className="visually-hidden">{t('storage.disks.caption')}</caption>
      <thead>
        <tr>
          <th scope="col">{t('storage.disks.name')}</th>
          <th scope="col" className={styles.optional}>
            {t('storage.disks.model')}
          </th>
          <th scope="col">{t('storage.disks.serial')}</th>
          <th scope="col" className={styles.number}>
            {t('storage.disks.size')}
          </th>
          <th scope="col">{t('storage.disks.status')}</th>
        </tr>
      </thead>
      <tbody>
        {disks.map(({ disk, guard }) => (
          <tr key={disk.id}>
            <th scope="row" className={styles.mono}>
              {disk.name}
            </th>
            <td className={styles.optional}>{disk.model ?? ''}</td>
            <td className={styles.mono}>{disk.serial ?? ''}</td>
            <td className={styles.number}>{formatBytes(disk.sizeBytes, i18n.language)}</td>
            <td>
              <div className={styles.verdict}>
                <Badge
                  tone={
                    guard.decision === 'allowed'
                      ? 'accent'
                      : guard.decision === 'refused'
                        ? 'neutral'
                        : 'highlight'
                  }
                >
                  {t(`storage.decision.${guard.decision}`)}
                </Badge>
                <span className={styles.reason}>{guardReason(t, guard)}</span>
              </div>
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

export function guardReason(t: Translate, guard: StorageDisk['guard']): string {
  return (t as unknown as (key: string, options: object) => string)(
    `storage.reason.${guard.reason}`,
    {
      pool: guard.pool ?? '',
      contents: guard.contents.join(', '),
    },
  );
}
