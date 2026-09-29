import { useTranslation } from 'react-i18next';
import { useStore } from 'zustand';
import { formatBytes } from '../../desktop/format';
import type { TemperatureReading } from '../sdk/monitor';
import { Chart } from './Chart';
import styles from './Monitor.module.css';
import { deviceNames, peak, seriesOf, type Point, type Range } from './series';
import type { MonitorStore } from './store';

const RANGE_TEXT = { 60: 'minute', 600: 'tenMinutes', 3600: 'hour' } as const;

function percent(value: number, locale: string): string {
  return new Intl.NumberFormat(locale, {
    style: 'percent',
    maximumFractionDigits: value < 10 ? 1 : 0,
  }).format(value / 100);
}

function celsius(value: number, locale: string): string {
  return new Intl.NumberFormat(locale, {
    style: 'unit',
    unit: 'celsius',
    maximumFractionDigits: 1,
  }).format(value);
}

/** CPU, memory, network, disks and temperatures: the newest sample as text, the range as charts. */
export function Performance({ monitor, range }: { monitor: MonitorStore; range: Range }) {
  const { t, i18n } = useTranslation('monitor');
  const points = useStore(monitor.store, (state) => state.points);
  const latest = useStore(monitor.store, (state) => state.latest);
  const memoryTotal = useStore(monitor.store, (state) => state.memoryTotalBytes);
  const locale = i18n.language;
  if (latest === null || memoryTotal === null) {
    return (
      <p className={styles.muted} role="status">
        {t('measuring')}
      </p>
    );
  }
  const end = points.at(-1)?.t ?? Date.parse(latest.at);
  const start = end - range * 1000;
  const rangeText = t(`rangeText.${RANGE_TEXT[range]}`);
  const bytes = (value: number) => formatBytes(value, locale);
  const rate = (value: number) => t('rate', { value: bytes(value) });
  const series = (value: (point: Point) => number | null) => seriesOf(points, end, range, value);
  const memory = latest.memory;
  const cache = memory.cacheBytes;
  const arc = memory.arcBytes ?? 0;
  const share = (value: number) => `${String((value / memory.totalBytes) * 100)}%`;
  const networkNames = deviceNames(points, (point) => point.network);
  const diskNames = deviceNames(points, (point) => point.disks);

  const cpu = series((point) => point.cpu);
  const used = series((point) => point.memoryUsed);
  const arcSeries = series((point) => point.arc);

  return (
    <div className={styles.grid}>
      <section className={styles.card} aria-labelledby="monitor-cpu">
        <div className={styles.cardHeader}>
          <h2 id="monitor-cpu" className={styles.cardTitle}>
            {t('cpu.title')}
          </h2>
          <span className={styles.big}>
            {t('cpu.now', { value: percent(latest.cpu.percent, locale) })}
          </span>
        </div>
        <Chart
          main={cpu}
          start={start}
          end={end}
          max={100}
          maxLabel={percent(100, locale)}
          label={t('cpu.chart', { range: rangeText, value: percent(latest.cpu.percent, locale) })}
        />
        {latest.cpu.cores.length > 1 && (
          <ul className={styles.cores} aria-label={t('cpu.cores')}>
            {latest.cpu.cores.map((core, index) => (
              <li key={index} className={styles.core}>
                <span>{t('cpu.core', { index })}</span>
                <span className={styles.number}>{percent(core, locale)}</span>
                <span className={styles.meter} aria-hidden="true">
                  <span className={styles.meterFill} style={{ width: `${String(core)}%` }} />
                </span>
              </li>
            ))}
          </ul>
        )}
      </section>

      <section className={styles.card} aria-labelledby="monitor-memory">
        <div className={styles.cardHeader}>
          <h2 id="monitor-memory" className={styles.cardTitle}>
            {t('memory.title')}
          </h2>
          <span className={styles.big}>
            {t('memory.now', { used: bytes(memory.usedBytes), total: bytes(memory.totalBytes) })}
          </span>
        </div>
        <div className={styles.stack} aria-hidden="true">
          <span className={styles.swatchMain} style={{ width: share(memory.usedBytes) }} />
          <span className={styles.swatchSecond} style={{ width: share(arc) }} />
          <span className={styles.swatchCache} style={{ width: share(cache) }} />
        </div>
        <ul className={styles.legend} aria-label={t('memory.parts')}>
          <li className={styles.legendItem}>
            <span className={`${styles.swatch} ${styles.swatchMain}`} aria-hidden="true" />
            {t('memory.used')} {bytes(memory.usedBytes)}
          </li>
          {memory.arcBytes !== null && (
            <li className={styles.legendItem}>
              <span className={`${styles.swatch} ${styles.swatchSecond}`} aria-hidden="true" />
              {t('memory.arc')} {bytes(memory.arcBytes)}
            </li>
          )}
          <li className={styles.legendItem}>
            <span className={`${styles.swatch} ${styles.swatchCache}`} aria-hidden="true" />
            {t('memory.cache')} {bytes(cache)}
          </li>
          <li className={styles.legendItem}>
            <span className={`${styles.swatch} ${styles.swatchFree}`} aria-hidden="true" />
            {t('memory.free')} {bytes(memory.freeBytes)}
          </li>
        </ul>
        <Chart
          main={used}
          {...(memory.arcBytes === null ? {} : { second: arcSeries })}
          start={start}
          end={end}
          max={memoryTotal}
          maxLabel={bytes(memoryTotal)}
          label={t('memory.chart', { range: rangeText, value: bytes(memory.usedBytes) })}
        />
        <p className={styles.note}>
          {memory.swapTotalBytes === 0
            ? t('memory.noSwap')
            : t('memory.swap', {
                used: bytes(memory.swapUsedBytes),
                total: bytes(memory.swapTotalBytes),
              })}
        </p>
        {memory.arcBytes !== null && <p className={styles.note}>{t('memory.arcHint')}</p>}
      </section>

      <section className={styles.card} aria-labelledby="monitor-network">
        <h2 id="monitor-network" className={styles.cardTitle}>
          {t('network.title')}
        </h2>
        {latest.network.length === 0 && <p className={styles.muted}>{t('network.none')}</p>}
        {networkNames
          .filter((name) => latest.network.some((rate) => rate.name === name))
          .map((name) => {
            const now = latest.network.find((candidate) => candidate.name === name);
            const received = series((point) => point.network[name]?.[0] ?? null);
            const sent = series((point) => point.network[name]?.[1] ?? null);
            return (
              <div key={name} className={styles.device}>
                <div className={styles.deviceHeader}>
                  <h3 className={`${styles.mono} ${styles.muted}`}>{name}</h3>
                  <ul className={styles.legend}>
                    <li className={styles.legendItem}>
                      <span
                        className={`${styles.swatch} ${styles.swatchMain}`}
                        aria-hidden="true"
                      />
                      {t('network.receive', { value: rate(now?.receiveBytesPerSecond ?? 0) })}
                    </li>
                    <li className={styles.legendItem}>
                      <span
                        className={`${styles.swatch} ${styles.swatchSecond}`}
                        aria-hidden="true"
                      />
                      {t('network.send', { value: rate(now?.sendBytesPerSecond ?? 0) })}
                    </li>
                  </ul>
                </div>
                <DeviceChart
                  main={received}
                  second={sent}
                  start={start}
                  end={end}
                  maxLabel={(max) => rate(max)}
                  label={t('network.chart', {
                    name,
                    range: rangeText,
                    received: rate(now?.receiveBytesPerSecond ?? 0),
                    sent: rate(now?.sendBytesPerSecond ?? 0),
                  })}
                />
              </div>
            );
          })}
      </section>

      <section className={styles.card} aria-labelledby="monitor-disks">
        <h2 id="monitor-disks" className={styles.cardTitle}>
          {t('disks.title')}
        </h2>
        {latest.disks.length === 0 && <p className={styles.muted}>{t('disks.none')}</p>}
        {diskNames
          .filter((name) => latest.disks.some((rate) => rate.name === name))
          .map((name) => {
            const now = latest.disks.find((candidate) => candidate.name === name);
            const read = series((point) => point.disks[name]?.[0] ?? null);
            const written = series((point) => point.disks[name]?.[1] ?? null);
            return (
              <div key={name} className={styles.device}>
                <div className={styles.deviceHeader}>
                  <h3 className={`${styles.mono} ${styles.muted}`}>{name}</h3>
                  <ul className={styles.legend}>
                    <li className={styles.legendItem}>
                      <span
                        className={`${styles.swatch} ${styles.swatchMain}`}
                        aria-hidden="true"
                      />
                      {t('disks.read', { value: rate(now?.readBytesPerSecond ?? 0) })}
                    </li>
                    <li className={styles.legendItem}>
                      <span
                        className={`${styles.swatch} ${styles.swatchSecond}`}
                        aria-hidden="true"
                      />
                      {t('disks.write', { value: rate(now?.writeBytesPerSecond ?? 0) })}
                    </li>
                    <li className={styles.legendItem}>
                      {t('disks.busy', { value: percent(now?.busyPercent ?? 0, locale) })}
                    </li>
                  </ul>
                </div>
                <DeviceChart
                  main={read}
                  second={written}
                  start={start}
                  end={end}
                  maxLabel={(max) => rate(max)}
                  label={t('disks.chart', {
                    name,
                    range: rangeText,
                    read: rate(now?.readBytesPerSecond ?? 0),
                    written: rate(now?.writeBytesPerSecond ?? 0),
                  })}
                />
              </div>
            );
          })}
        <p className={styles.note}>{t('disks.hint')}</p>
      </section>

      <Temperatures readings={latest.temperatures} locale={locale} />
    </div>
  );
}

/** Two rates on one scale, from 0 to the larger one's peak in the range (at least 1 KB/s). */
function DeviceChart({
  main,
  second,
  start,
  end,
  label,
  maxLabel,
}: {
  main: ReturnType<typeof seriesOf>;
  second: ReturnType<typeof seriesOf>;
  start: number;
  end: number;
  label: string;
  maxLabel: (max: number) => string;
}) {
  const max = peak([main, second], 1000);
  return (
    <Chart
      main={main}
      second={second}
      start={start}
      end={end}
      max={max}
      maxLabel={maxLabel(max)}
      label={label}
    />
  );
}

function Temperatures({ readings, locale }: { readings: TemperatureReading[]; locale: string }) {
  const { t } = useTranslation('monitor');
  return (
    <section className={styles.card} aria-labelledby="monitor-temperatures">
      <h2 id="monitor-temperatures" className={styles.cardTitle}>
        {t('temperatures.title')}
      </h2>
      {readings.length === 0 ? (
        <p className={styles.muted}>{t('temperatures.none')}</p>
      ) : (
        <table className={styles.table}>
          <caption className="visually-hidden">{t('temperatures.caption')}</caption>
          <thead>
            <tr>
              <th scope="col">{t('temperatures.sensor')}</th>
              <th scope="col" className={styles.number}>
                {t('temperatures.value')}
              </th>
              <th scope="col" className={styles.number}>
                {t('temperatures.limits')}
              </th>
            </tr>
          </thead>
          <tbody>
            {readings.map((reading, index) => {
              const critical =
                reading.criticalCelsius !== null && reading.celsius >= reading.criticalCelsius;
              const high =
                !critical && reading.highCelsius !== null && reading.celsius >= reading.highCelsius;
              return (
                <tr key={`${reading.chip}-${String(index)}`}>
                  <th scope="row">
                    <span className={styles.mono}>{reading.chip}</span>
                    {reading.label !== null && ` ${reading.label}`}
                  </th>
                  <td
                    className={`${styles.number} ${critical ? styles.hot : high ? styles.warm : ''}`}
                  >
                    {celsius(reading.celsius, locale)}
                    {(critical || high) && (
                      <span className="visually-hidden">
                        {' '}
                        {critical ? t('temperatures.critical') : t('temperatures.high')}
                      </span>
                    )}
                  </td>
                  <td className={styles.number}>
                    {[reading.highCelsius, reading.criticalCelsius]
                      .map((value) => (value === null ? '' : celsius(value, locale)))
                      .filter((text) => text !== '')
                      .join(' / ')}
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      )}
    </section>
  );
}
