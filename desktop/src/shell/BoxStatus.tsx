import { IconCpu, IconNetwork } from '../ui/icons';
import { useEffect } from 'react';
import { Button as AriaButton } from 'react-aria-components';
import { useTranslation } from 'react-i18next';
import { formatBytes } from '../desktop/format';
import { realtime, useRealtime } from '../realtime/connection';
import styles from './Taskbar.module.css';

// The order in which a pool's state is worth showing: the worst one stands for the box.
const SEVERITY = ['ONLINE', 'UNKNOWN', 'DEGRADED'];

function severity(state: string): number {
  const index = SEVERITY.indexOf(state);
  return index < 0 ? SEVERITY.length : index;
}

/**
 * The box's state in the taskbar for administrators (D394): the pool that needs the most attention, the processor and
 * the network, live from the topics the Storage app and the Resource Monitor read. Each chip opens its app.
 */
export function BoxStatus({
  locale,
  onOpen,
}: {
  locale: string;
  onOpen: (app: 'storage' | 'monitor') => void;
}) {
  const { t } = useTranslation();
  // Subscribing is synchronization with the WebSocket, not data fetching.
  useEffect(() => {
    const stops = [realtime.subscribe('storage/pools'), realtime.subscribe('system/resources')];
    return () => {
      for (const stop of stops) {
        stop();
      }
    };
  }, []);
  const pools = useRealtime((state) => state.data['storage/pools']?.pools);
  const sample = useRealtime((state) => state.data['system/resources']?.latest);

  const pool = pools?.toSorted((a, b) => severity(b.state) - severity(a.state))[0];
  const poolState = pool === undefined ? '' : t('taskbar.poolState', { state: pool.state });
  const tone =
    pool === undefined || pool.state === 'ONLINE'
      ? 'success'
      : pool.state === 'DEGRADED'
        ? 'warning'
        : 'danger';
  const cpu =
    sample === undefined || sample === null
      ? undefined
      : new Intl.NumberFormat(locale, { style: 'percent', maximumFractionDigits: 0 }).format(
          sample.cpu.percent / 100,
        );
  const rate =
    sample === undefined || sample === null
      ? undefined
      : t('taskbar.rate', {
          size: formatBytes(
            sample.network.reduce(
              (sum, entry) => sum + entry.receiveBytesPerSecond + entry.sendBytesPerSecond,
              0,
            ),
            locale,
          ),
        });

  return (
    <>
      {pool !== undefined && (
        <AriaButton
          className={styles.chip}
          aria-label={t('taskbar.poolStatus', { name: pool.name, state: poolState })}
          onPress={() => {
            onOpen('storage');
          }}
        >
          <span className={styles.dot} data-tone={tone} aria-hidden />
          <b>{pool.name}</b>
          <span>{poolState}</span>
        </AriaButton>
      )}
      {cpu !== undefined && (
        <AriaButton
          className={styles.chip}
          aria-label={t('taskbar.cpuStatus', { value: cpu })}
          onPress={() => {
            onOpen('monitor');
          }}
        >
          <IconCpu size={16} stroke={1.75} aria-hidden />
          <b>{cpu}</b>
        </AriaButton>
      )}
      {rate !== undefined && (
        <AriaButton
          className={styles.chip}
          aria-label={t('taskbar.networkStatus', { value: rate })}
          onPress={() => {
            onOpen('monitor');
          }}
        >
          <IconNetwork size={16} stroke={1.75} aria-hidden />
          <b>{rate}</b>
        </AriaButton>
      )}
      <span className={styles.separator} aria-hidden />
    </>
  );
}
