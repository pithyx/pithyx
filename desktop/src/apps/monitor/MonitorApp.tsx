import { AppFrame, NavList, type NavGroup } from '@pithyx/ui';
import { useEffect, useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useStore } from 'zustand';
import { formatBytes } from '../../desktop/format';
import {
  IconActivity,
  IconCpu,
  IconDatabase,
  IconLayoutGrid,
  IconListDetails,
  IconNetwork,
  IconTemperature,
} from '../../ui/icons';
import type { SdkAppProps } from '../sdk/CoreSdkApp';
import { MONITOR_PROCESSES, MONITOR_RESOURCES, onMonitor, watchMonitor } from '../sdk/monitor';
import styles from './Monitor.module.css';
import { Performance, type Part } from './Performance';
import { Processes } from './Processes';
import { sortProcesses } from './processes';
import { RANGES, type Range } from './series';
import { createMonitorStore, type MonitorStore } from './store';
import { useFormatLocale } from '../../i18n';

type Place = Part | 'processes';

const RANGE_KEYS = { 60: 'minute', 600: 'tenMinutes', 3600: 'hour' } as const;

const PLACES: readonly { id: Place; icon: typeof IconActivity }[] = [
  { id: 'overview', icon: IconLayoutGrid },
  { id: 'cpu', icon: IconCpu },
  { id: 'memory', icon: IconActivity },
  { id: 'disks', icon: IconDatabase },
  { id: 'network', icon: IconNetwork },
  { id: 'temperatures', icon: IconTemperature },
  { id: 'processes', icon: IconListDetails },
];

/** How many processes the overview lists. */
const TOP = 6;

/**
 * Resource Monitor (D394): a navigation by part (overview, CPU, memory, disks, network, temperatures, processes), the
 * time range in the toolbar and the uptime and load in the status bar. The daemon measures only while a window
 * watches, and processes only while the overview or their page is shown; the history reaches back one hour.
 */
export function MonitorApp({ app }: SdkAppProps) {
  const { t } = useTranslation('monitor');
  const [monitor] = useState(createMonitorStore);
  const [place, setPlace] = useState<Place>('overview');
  const [view, setView] = useState<'navigation' | 'page'>('page');
  const [range, setRange] = useState<Range>(60);
  const rangeName = useId();

  // Following the daemon's topics is synchronization with the WebSocket; listening comes first so the snapshot is
  // not lost.
  useEffect(() => {
    const stops = [
      onMonitor(app, MONITOR_RESOURCES, monitor.receiveResources),
      onMonitor(app, MONITOR_PROCESSES, monitor.receiveProcesses),
    ];
    return () => {
      stops.forEach((stop) => {
        stop();
      });
    };
  }, [app, monitor]);
  const wantsProcesses = place === 'overview' || place === 'processes';
  useEffect(() => {
    void watchMonitor(app, { resources: true, processes: wantsProcesses });
  }, [app, wantsProcesses]);

  const groups: NavGroup[] = [
    {
      id: 'parts',
      items: PLACES.map(({ id, icon: Icon }) => ({
        id,
        label: t(`views.${id}`),
        icon: <Icon size={18} stroke={1.75} aria-hidden />,
      })),
    },
  ];

  return (
    <AppFrame
      className={styles.frame}
      view={view}
      onViewChange={setView}
      navigation={
        <NavList
          aria-label={t('views.label')}
          searchPlaceholder={t('views.search')}
          groups={groups}
          selectedId={place}
          onSelect={(id) => {
            const next = PLACES.find((entry) => entry.id === id);
            if (next !== undefined) {
              setPlace(next.id);
              setView('page');
            }
          }}
        />
      }
      title={t(`views.${place}`)}
      toolbar={
        place === 'processes' ? undefined : (
          <fieldset className={styles.range}>
            <legend className="visually-hidden">{t('range.label')}</legend>
            <span className={styles.segments}>
              {RANGES.map((candidate) => (
                <label key={candidate} className={styles.segmentLabel}>
                  <input
                    type="radio"
                    name={rangeName}
                    className={styles.segmentInput}
                    checked={range === candidate}
                    onChange={() => {
                      setRange(candidate);
                    }}
                  />
                  <span className={styles.segment}>{t(`range.${RANGE_KEYS[candidate]}`)}</span>
                </label>
              ))}
            </span>
          </fieldset>
        )
      }
      statusBar={<Summary monitor={monitor} />}
    >
      <div className={styles.content}>
        {place === 'processes' ? (
          <Processes monitor={monitor} />
        ) : (
          <>
            <Performance monitor={monitor} range={range} part={place} />
            {place === 'overview' && <TopProcesses monitor={monitor} />}
            <p className={styles.note}>{t('history')}</p>
          </>
        )}
      </div>
    </AppFrame>
  );
}

/** The processes that use the most CPU now, with their user and memory. */
function TopProcesses({ monitor }: { monitor: MonitorStore }) {
  const { t } = useTranslation('monitor');
  const locale = useFormatLocale();
  const list = useStore(monitor.store, (state) => state.processes);
  if (list === null) {
    return null;
  }
  const top = sortProcesses(list.processes, 'cpu', 'descending', '', locale).slice(0, TOP);
  const percent = new Intl.NumberFormat(locale, { style: 'percent', maximumFractionDigits: 1 });
  return (
    <section className={styles.top} aria-labelledby="monitor-top">
      <h2 id="monitor-top" className={styles.topTitle}>
        {t('top.title')}
      </h2>
      <table className={styles.table}>
        <caption className="visually-hidden">{t('top.title')}</caption>
        <thead>
          <tr>
            <th scope="col">{t('processes.name')}</th>
            <th scope="col">{t('processes.user')}</th>
            <th scope="col" className={styles.number}>
              {t('processes.cpu')}
            </th>
            <th scope="col" className={styles.number}>
              {t('processes.memory')}
            </th>
          </tr>
        </thead>
        <tbody>
          {top.map((process) => (
            <tr key={process.pid}>
              <th scope="row" className={styles.topName}>
                {process.name}
              </th>
              <td className={styles.muted}>{process.user}</td>
              <td className={styles.number}>
                <span className={styles.topCpu}>
                  {percent.format((process.cpuPercent ?? 0) / 100)}
                  <span className={styles.meter} aria-hidden="true">
                    <span
                      className={styles.meterFill}
                      style={{ width: `${String(Math.min(100, process.cpuPercent ?? 0))}%` }}
                    />
                  </span>
                </span>
              </td>
              <td className={styles.number}>{formatBytes(process.memoryBytes, locale)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </section>
  );
}

function Summary({ monitor }: { monitor: MonitorStore }) {
  const { t } = useTranslation('monitor');
  const locale = useFormatLocale();
  const uptime = useStore(monitor.store, (state) => state.latest?.uptimeSeconds ?? null);
  const load = useStore(monitor.store, (state) => state.latest?.loadAverage ?? null);
  if (uptime === null || load === null) {
    return null;
  }
  const number = new Intl.NumberFormat(locale, {
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  });
  const minutes = Math.floor(uptime / 60);
  return (
    <p className={styles.summary}>
      <span>
        {t('summary.uptime', {
          days: Math.floor(minutes / 1440),
          hours: Math.floor((minutes % 1440) / 60),
          minutes: minutes % 60,
        })}
      </span>
      <span>
        {t('summary.load', {
          one: number.format(load[0] ?? 0),
          five: number.format(load[1] ?? 0),
          fifteen: number.format(load[2] ?? 0),
        })}
      </span>
    </p>
  );
}
