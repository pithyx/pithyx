import { useEffect, useId, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useStore } from 'zustand';
import type { SdkAppProps } from '../sdk/CoreSdkApp';
import { MONITOR_PROCESSES, MONITOR_RESOURCES, onMonitor, watchMonitor } from '../sdk/monitor';
import styles from './Monitor.module.css';
import { Performance } from './Performance';
import { Processes } from './Processes';
import { RANGES, type Range } from './series';
import { createMonitorStore, type MonitorStore } from './store';
import { useFormatLocale } from '../../i18n';

type View = 'performance' | 'processes';

const RANGE_KEYS = { 60: 'minute', 600: 'tenMinutes', 3600: 'hour' } as const;

/**
 * Resource Monitor: CPU, memory, network, disks and temperatures live with the last hour, and the processes. The
 * daemon measures only while a window watches, and processes only while their view is shown.
 */
export function MonitorApp({ app }: SdkAppProps) {
  const { t } = useTranslation('monitor');
  const [monitor] = useState(createMonitorStore);
  const [view, setView] = useState<View>('performance');
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
  useEffect(() => {
    void watchMonitor(app, { resources: true, processes: view === 'processes' });
  }, [app, view]);

  return (
    <div className={styles.app}>
      <div className={styles.toolbar}>
        <div className={styles.segments} role="group" aria-label={t('views.label')}>
          {(['performance', 'processes'] as const).map((candidate) => (
            <button
              key={candidate}
              type="button"
              className={styles.segment}
              aria-pressed={view === candidate}
              onClick={() => {
                setView(candidate);
              }}
            >
              {t(`views.${candidate}`)}
            </button>
          ))}
        </div>
        <Summary monitor={monitor} />
        {view === 'performance' && (
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
        )}
      </div>
      {/* A scrolling region must be reachable with the keyboard. */}
      <div className={styles.body} role="region" aria-label={t(`views.${view}`)} tabIndex={0}>
        {view === 'performance' ? (
          <>
            <Performance monitor={monitor} range={range} />
            <p className={styles.note}>{t('history')}</p>
          </>
        ) : (
          <Processes monitor={monitor} />
        )}
      </div>
    </div>
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
