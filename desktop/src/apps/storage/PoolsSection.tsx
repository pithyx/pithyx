import type { PithyxApp } from '@pithyx/sdk';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { formatBytes } from '../../desktop/format';
import { ErrorNotice } from '../../errors/ErrorNotice';
import {
  Button,
  Card,
  cx,
  Meter,
  ProgressBar,
  StatusBadge,
  SummaryTile,
  SummaryTiles,
  Toolbar,
} from '@pithyx/ui';
import { IconDatabase, IconPlus } from '../../ui/icons';
import {
  onInternal,
  STORAGE_JOB,
  type Dataset,
  type Job,
  type Pool,
  type Vdev,
} from '../sdk/internal';
import { isHiddenDataset } from './datasets';
import { PoolActionDialog, type PoolAction } from './PoolActionDialog';
import { PoolChangeDialog, type PoolChange } from './PoolChangeDialog';
import { isFinished } from './PlanRun';
import { PoolWizard } from './PoolWizard';
import {
  ChangeKeyDialog,
  ForgetKeyDialog,
  RevealKeyDialog,
  UnlockDialog,
  WriteUsbKeyDialog,
} from './PoolEncryption';
import {
  describeProblem,
  formatDate,
  stateKey,
  stateStatus,
  useDisks,
  usePools,
  type Translate,
} from './shared';
import styles from './Storage.module.css';
import { useFormatLocale } from '../../i18n';

/** Pools with their layout, health and fill level, the pool wizard, and repair and maintenance through plans. */
export function PoolsSection({ app }: { app: PithyxApp }) {
  const { t } = useTranslation('storage');
  const [creating, setCreating] = useState(false);
  const [action, setAction] = useState<{ pool: Pool; vdev?: Vdev; action: PoolAction } | null>(
    null,
  );
  const [change, setChange] = useState<{ pool: Pool; vdev?: Vdev; action: PoolChange } | null>(
    null,
  );
  const [keyAction, setKeyAction] = useState<{ pool: Pool; action: KeyAction } | null>(null);
  // Pools whose destroy job runs, by name; the card says so until the pool is gone or the job failed.
  const [deleting, setDeleting] = useState<Readonly<Record<string, number>>>({});
  const pools = usePools(app);
  const disks = useDisks(app);
  useEffect(
    () =>
      onInternal(app, STORAGE_JOB, (data) => {
        const job = data as Job;
        if (isFinished(job)) {
          setDeleting((current) =>
            Object.fromEntries(Object.entries(current).filter(([, id]) => id !== job.id)),
          );
        }
      }),
    [app],
  );

  return (
    <div className={styles.storage}>
      <p className={styles.muted}>{t('intro')}</p>
      {pools.data !== undefined && pools.data.pools.length > 0 && (
        <PoolSummary pools={pools.data.pools} />
      )}
      <Toolbar aria-label={t('pools.toolbar')}>
        <Button
          variant="primary"
          onPress={() => {
            setCreating(true);
          }}
          isDisabled={disks.data === undefined}
        >
          <IconPlus size={18} stroke={1.75} aria-hidden />
          {t('create.button')}
        </Button>
      </Toolbar>
      {disks.data?.environment === 'dev' && <p className={styles.notice}>{t('dev')}</p>}
      {pools.isError && <ErrorNotice error={describeProblem(pools.error)} />}
      {pools.data?.pools.length === 0 && <p className={styles.muted}>{t('pools.empty')}</p>}
      <div className={styles.pools}>
        {pools.data?.pools.map((pool) => (
          <PoolCard
            key={pool.name}
            pool={pool}
            deleting={pool.name in deleting}
            datasets={pools.data.datasets.filter(
              (dataset) => dataset.pool === pool.name && !isHiddenDataset(pool.name, dataset.name),
            )}
            onAction={(poolAction, vdev) => {
              setAction({ pool, action: poolAction, ...(vdev === undefined ? {} : { vdev }) });
            }}
            onChange={(poolChange, vdev) => {
              setChange({ pool, action: poolChange, ...(vdev === undefined ? {} : { vdev }) });
            }}
            onKey={(keyChange) => {
              setKeyAction({ pool, action: keyChange });
            }}
          />
        ))}
      </div>

      {creating && disks.data !== undefined && (
        <PoolWizard
          app={app}
          disks={disks.data.disks}
          onClose={() => {
            setCreating(false);
          }}
        />
      )}
      {keyAction?.action === 'unlock' && (
        <UnlockDialog
          app={app}
          pool={keyAction.pool}
          onClose={() => {
            setKeyAction(null);
          }}
        />
      )}
      {keyAction?.action === 'change' && (
        <ChangeKeyDialog
          app={app}
          pool={keyAction.pool}
          onClose={() => {
            setKeyAction(null);
          }}
        />
      )}
      {keyAction?.action === 'reveal' && (
        <RevealKeyDialog
          app={app}
          pool={keyAction.pool}
          onClose={() => {
            setKeyAction(null);
          }}
        />
      )}
      {keyAction?.action === 'usb' && (
        <WriteUsbKeyDialog
          app={app}
          pool={keyAction.pool}
          onClose={() => {
            setKeyAction(null);
          }}
        />
      )}
      {keyAction?.action === 'forget' && (
        <ForgetKeyDialog
          app={app}
          pool={keyAction.pool}
          onClose={() => {
            setKeyAction(null);
          }}
        />
      )}
      {change !== null && disks.data !== undefined && (
        <PoolChangeDialog
          app={app}
          pool={change.pool}
          {...(change.vdev === undefined ? {} : { vdev: change.vdev })}
          action={change.action}
          disks={disks.data.disks}
          onClose={() => {
            setChange(null);
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
          onStarted={(job) => {
            if (action.action === 'pool.destroy') {
              setDeleting((current) => ({ ...current, [action.pool.name]: job.id }));
            }
          }}
        />
      )}
    </div>
  );
}

/** The pools at a glance (D394): capacity, space used, the disks in them and the last scrub. */
function PoolSummary({ pools }: { pools: readonly Pool[] }) {
  const { t } = useTranslation('storage');
  const locale = useFormatLocale();
  const size = pools.reduce((sum, pool) => sum + pool.sizeBytes, 0);
  const used = pools.reduce((sum, pool) => sum + pool.allocatedBytes, 0);
  const disks = pools.flatMap((pool) =>
    pool.vdevs.flatMap((vdev) => (vdev.children.length === 0 ? [vdev] : vdev.children)),
  );
  const failing = disks.filter((disk) => disk.missing || stateStatus(disk.state) !== 'success');
  const scrubs = pools
    .map((pool) => pool.scan)
    .filter((scan) => scan !== null && scan.kind !== 'resilver' && scan.endedAt !== null)
    .sort((a, b) => (b?.endedAt ?? '').localeCompare(a?.endedAt ?? ''));
  const last = scrubs[0] ?? null;
  return (
    <SummaryTiles>
      <SummaryTile
        label={t('summary.capacity')}
        value={formatBytes(size, locale)}
        detail={t('summary.pools', { count: pools.length })}
      />
      <SummaryTile
        label={t('summary.used')}
        value={formatBytes(used, locale)}
        detail={new Intl.NumberFormat(locale, {
          style: 'percent',
          maximumFractionDigits: 0,
        }).format(size === 0 ? 0 : used / size)}
      />
      <SummaryTile
        label={t('summary.disks')}
        value={new Intl.NumberFormat(locale).format(disks.length)}
        detail={
          failing.length === 0
            ? t('summary.allHealthy')
            : t('summary.problems', { count: failing.length })
        }
      />
      <SummaryTile
        label={t('summary.lastScrub')}
        value={
          last?.endedAt == null
            ? t('summary.never')
            : new Intl.DateTimeFormat(locale, { dateStyle: 'medium' }).format(
                new Date(last.endedAt),
              )
        }
        {...(last === null ? {} : { detail: t('summary.errors', { count: last.errors }) })}
      />
    </SummaryTiles>
  );
}

type OnAction = (action: PoolAction, vdev?: Vdev) => void;
type OnChange = (action: PoolChange, vdev?: Vdev) => void;
type KeyAction = 'unlock' | 'change' | 'forget' | 'reveal' | 'usb';

/** Why a locked pool waits: a stored key that failed, its USB stick, the vault still at work, or the typed key. */
function lockedHint(t: Translate, encryption: NonNullable<Pool['encryption']>): string {
  if (encryption.vaultFailed) {
    return t('encryption.lockedVaultHint');
  }
  if (encryption.usbFailed) {
    return t('encryption.lockedUsbFailedHint');
  }
  if (encryption.keyInVault) {
    return t('encryption.opening');
  }
  return encryption.usbKey != null
    ? t('encryption.lockedUsbHint', { stick: encryption.usbKey.stick })
    : t('encryption.lockedHint');
}

function PoolCard({
  pool,
  deleting,
  datasets,
  onAction,
  onChange,
  onKey,
}: {
  pool: Pool;
  deleting: boolean;
  datasets: Dataset[];
  onAction: OnAction;
  onChange: OnChange;
  onKey: (action: KeyAction) => void;
}) {
  const { t } = useTranslation('storage');
  const locale = useFormatLocale();
  const encryption = pool.encryption ?? null;
  const data = pool.vdevs.filter((vdev) => vdev.role === 'data');
  const others = pool.vdevs.filter((vdev) => vdev.role !== 'data');
  const tone =
    pool.capacityPercent >= 90 ? 'danger' : pool.capacityPercent >= 80 ? 'warning' : 'accent';
  const used = formatBytes(pool.allocatedBytes, locale);
  const free = formatBytes(Math.max(0, pool.sizeBytes - pool.allocatedBytes), locale);
  const actions = (
    <div className={styles.poolActions}>
      {deleting ? (
        <p className={styles.muted} role="status">
          {t('pools.deletingHint')}
        </p>
      ) : pool.readOnly ? (
        <>
          <Button
            size="sm"
            variant="primary"
            onPress={() => {
              onChange('pool.import');
            }}
          >
            {t('change.import.button')}
          </Button>
          <Button
            size="sm"
            onPress={() => {
              onChange('pool.export');
            }}
          >
            {t('change.discard.button')}
          </Button>
        </>
      ) : (
        <>
          <Button
            size="sm"
            onPress={() => {
              onChange('pool.vdev.add');
            }}
          >
            {t('change.add.button')}
          </Button>
          <Button
            size="sm"
            onPress={() => {
              onAction('pool.spare.add');
            }}
          >
            {t('actions.spareAdd.button')}
          </Button>
          {encryption !== null && !encryption.locked && (
            <Button
              size="sm"
              onPress={() => {
                onKey('change');
              }}
            >
              {t('encryption.change')}
            </Button>
          )}
          {encryption !== null && (
            <Button
              size="sm"
              onPress={() => {
                onKey('usb');
              }}
            >
              {t('encryption.writeUsb')}
            </Button>
          )}
          {encryption?.keyInVault === true && (
            <Button
              size="sm"
              onPress={() => {
                onKey('reveal');
              }}
            >
              {t('encryption.reveal')}
            </Button>
          )}
          {encryption?.keyInVault === true && (
            <Button
              size="sm"
              onPress={() => {
                onKey('forget');
              }}
            >
              {t('encryption.forget')}
            </Button>
          )}
          <Button
            size="sm"
            variant="danger"
            onPress={() => {
              onAction('pool.destroy');
            }}
          >
            {t('actions.destroy.button')}
          </Button>
        </>
      )}
    </div>
  );
  return (
    // The article names the card for its region of the page; the card itself is a disclosure.
    <article className={styles.pool} aria-label={pool.name}>
      <Card
        icon={<IconDatabase size={24} stroke={1.5} />}
        title={<span className={styles.poolName}>{pool.name}</span>}
        badge={
          <span className={styles.badges}>
            {deleting ? (
              <StatusBadge>{t('pools.deleting')}</StatusBadge>
            ) : (
              <StatusBadge tone={stateStatus(pool.state)}>
                {t(`state.${stateKey(pool.state)}`)}
              </StatusBadge>
            )}
            {pool.readOnly && <StatusBadge>{t('pools.readOnly')}</StatusBadge>}
            {encryption !== null && (
              <StatusBadge tone={encryption.locked ? 'warning' : 'neutral'}>
                {t(encryption.locked ? 'encryption.locked' : 'encryption.encrypted')}
              </StatusBadge>
            )}
          </span>
        }
        facts={[
          data.map((vdev) => vdevLabel(t, vdev)).join(' + '),
          pool.scan === null ? null : scanText(t, pool, locale),
        ]
          .filter((part) => part !== null && part !== '')
          .join(' · ')}
        meter={
          <Meter
            aria-label={t('pools.space', { pool: pool.name })}
            value={pool.capacityPercent}
            start={t('pools.usedShort', { used })}
            end={t('pools.free', { free })}
            valueLabel={t('pools.used', {
              used,
              size: formatBytes(pool.sizeBytes, locale),
            })}
            tone={tone}
          />
        }
        defaultExpanded
        actions={actions}
      >
        <div className={styles.poolDetails}>
          {pool.readOnly && <p className={styles.notice}>{t('pools.readOnlyHint')}</p>}
          {encryption?.locked === true && !pool.readOnly && (
            <div className={cx(styles.notice, styles.lockedNotice)}>
              <p>{lockedHint(t, encryption)}</p>
              <Button
                size="sm"
                variant="primary"
                onPress={() => {
                  onKey('unlock');
                }}
              >
                {t('encryption.unlock')}
              </Button>
            </div>
          )}
          {encryption?.unlockedFromUsb === true && (
            <p className={styles.notice} role="status">
              {t('encryption.unlockedFromUsb')}
            </p>
          )}
          {encryption !== null && (
            <p className={styles.muted}>
              {encryption.keyInVault
                ? t('encryption.inVault')
                : encryption.usbKey != null
                  ? t('encryption.onStick', { stick: encryption.usbKey.stick })
                  : t('encryption.byHand')}
              {encryption.keyInVault && encryption.usbKey != null && (
                <> {t('encryption.alsoOnStick', { stick: encryption.usbKey.stick })}</>
              )}
            </p>
          )}
          <Expansion pool={pool} />
          {pool.state !== 'ONLINE' && <p className={styles.notice}>{t('pools.degradedHint')}</p>}
          <ul className={styles.vdevs} aria-label={t('pools.devices', { pool: pool.name })}>
            {[...data, ...others].map((vdev) => (
              <VdevItem
                key={vdev.name}
                vdev={vdev}
                readOnly={pool.readOnly || deleting}
                onAction={onAction}
                onChange={onChange}
              />
            ))}
          </ul>
          <details className={styles.datasets} open={pool.readOnly}>
            <summary>{t('datasets.title', { count: datasets.length })}</summary>
            <ul>
              {datasets.map((dataset) => (
                <li key={dataset.name}>
                  <span className={styles.mono}>{dataset.name}</span>
                  <span className={styles.muted}>
                    {' '}
                    {dataset.mountPoint ?? t('datasets.notMounted')} ·{' '}
                    {formatBytes(dataset.usedBytes, locale)}
                  </span>
                </li>
              ))}
            </ul>
          </details>
        </div>
      </Card>
    </article>
  );
}

/** A running RAID-Z expansion with its progress, or when the last one ended. */
function Expansion({ pool }: { pool: Pool }) {
  const { t } = useTranslation('storage');
  const locale = useFormatLocale();
  const expansion = pool.expansion;
  if (expansion === null || expansion === undefined) {
    return null;
  }
  const vdev =
    pool.vdevs.find((candidate) => candidate.vdevGuid === expansion.vdevGuid)?.name ?? '';
  if (expansion.state !== 'scanning') {
    return expansion.state === 'finished' && expansion.endedAt !== null ? (
      <p className={styles.muted}>
        {t('expansion.finished', { vdev, date: formatDate(expansion.endedAt, locale) })}
      </p>
    ) : null;
  }
  const percent =
    expansion.toReflowBytes === 0
      ? 0
      : Math.min(100, Math.floor((expansion.reflowedBytes / expansion.toReflowBytes) * 100));
  return (
    <>
      <ProgressBar label={t('expansion.running', { vdev })} value={percent} />
      {expansion.waitingForResilver && (
        <p className={styles.notice}>{t('expansion.waiting', { vdev })}</p>
      )}
    </>
  );
}

function VdevItem({
  vdev,
  readOnly,
  onAction,
  onChange,
}: {
  vdev: Vdev;
  readOnly: boolean;
  onAction: OnAction;
  onChange: OnChange;
}) {
  const { t } = useTranslation('storage');
  const leaves = vdev.children.length === 0 ? [vdev] : vdev.children;
  return (
    <li className={styles.vdev}>
      <span className={styles.vdevType}>
        {vdev.role === 'data' ? vdevLabel(t, vdev) : t(`role.${roleKey(vdev.role)}`)}
        {!readOnly && vdev.role === 'data' && vdev.type === 'raidz' && (
          <Button
            size="sm"
            variant="quiet"
            onPress={() => {
              onChange('pool.raidz.expand', vdev);
            }}
          >
            {t('change.expand.button')}
          </Button>
        )}
      </span>
      <ul className={styles.leaves}>
        {leaves.map((leaf) => (
          <li key={leaf.name} className={styles.leaf} data-state={leaf.state}>
            <span className={styles.mono}>{leaf.name}</span>
            <StatusBadge tone={leaf.missing ? 'danger' : stateStatus(leaf.state)}>
              {leaf.missing ? t('state.missing') : t(`state.${stateKey(leaf.state)}`)}
            </StatusBadge>
            <span className={styles.leafActions}>
              {readOnly ? null : vdev.role === 'spare' ? (
                <Button
                  size="sm"
                  variant="quiet"
                  onPress={() => {
                    onAction('pool.spare.remove', leaf);
                  }}
                >
                  {t('actions.spareRemove.button')}
                </Button>
              ) : (
                <>
                  <Button
                    size="sm"
                    variant="quiet"
                    onPress={() => {
                      onAction('pool.replace', leaf);
                    }}
                  >
                    {t('actions.replace.button')}
                  </Button>
                </>
              )}
            </span>
            {leaf.readErrors + leaf.writeErrors + leaf.checksumErrors > 0 && (
              <span className={styles.errors}>
                {t('pools.errors', {
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

const ROLES = ['log', 'cache', 'special', 'dedup', 'spare'] as const;

function roleKey(role: string): (typeof ROLES)[number] {
  return ROLES.find((known) => known === role) ?? 'special';
}

function vdevLabel(t: Translate, vdev: Vdev): string {
  switch (vdev.type) {
    case 'mirror':
      return t('vdev.mirror', { count: vdev.children.length });
    case 'raidz':
      return t('vdev.raidz', {
        parity: vdev.parityDisks ?? 1,
        count: vdev.children.length,
      });
    case 'disk':
    case 'file':
      return t('vdev.single');
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
    return scan.paused ? t('scan.paused', { percent }) : t(`scan.${kind}Running`, { percent });
  }
  return scan.state === 'finished'
    ? t(`scan.${kind}Finished`, { date, errors: scan.errors })
    : t('scan.canceled');
}
