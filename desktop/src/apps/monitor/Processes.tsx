import { useId, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useStore } from 'zustand';
import { formatBytes } from '../../desktop/format';
import styles from './Monitor.module.css';
import {
  defaultDirection,
  sortProcesses,
  visibleRows,
  type Direction,
  type SortKey,
} from './processes';
import type { MonitorStore } from './store';
import { useFormatLocale } from '../../i18n';

const COLUMNS: readonly SortKey[] = ['name', 'pid', 'user', 'cpu', 'memory'];
const NUMERIC = new Set<SortKey>(['pid', 'cpu', 'memory']);

/**
 * Every process, filtered and sorted in the window. The list is virtualized: a box runs a few hundred processes, and
 * only the rows in view are rendered each second.
 */
export function Processes({ monitor }: { monitor: MonitorStore }) {
  const { t } = useTranslation('monitor');
  const list = useStore(monitor.store, (state) => state.processes);
  const [filter, setFilter] = useState('');
  const [sort, setSort] = useState<{ key: SortKey; direction: Direction }>({
    key: 'cpu',
    direction: 'descending',
  });
  const scroll = useRef<HTMLDivElement>(null);
  const filterId = useId();
  const hintId = useId();
  const locale = useFormatLocale();
  const rows =
    list === null ? [] : sortProcesses(list.processes, sort.key, sort.direction, filter, locale);
  const rowHeight = matchMedia('(pointer: coarse)').matches ? 44 : 32;
  const [scrollTop, setScrollTop] = useState(0);
  // The table is never taller than the browser window, so that bounds the rows to render without measuring.
  const shown = visibleRows(rows.length, rowHeight, scrollTop, window.innerHeight);
  const percent = new Intl.NumberFormat(locale, { style: 'percent', maximumFractionDigits: 1 });

  return (
    <div className={styles.processes}>
      <div className={styles.filter}>
        <div className={styles.filterField}>
          <label htmlFor={filterId}>{t('processes.filter')}</label>
          <input
            id={filterId}
            className={styles.input}
            type="search"
            value={filter}
            autoComplete="off"
            spellCheck={false}
            onChange={(event) => {
              setFilter(event.currentTarget.value);
            }}
          />
        </div>
        {list !== null && (
          <p className={styles.muted} role="status">
            {t('processes.count', { total: list.processes.length, shown: rows.length })}
          </p>
        )}
      </div>
      {list === null ? (
        <p className={styles.muted} role="status">
          {t('processes.loading')}
        </p>
      ) : (
        <div
          ref={scroll}
          className={styles.processTable}
          role="table"
          aria-label={t('processes.caption')}
          aria-rowcount={rows.length + 1}
          aria-describedby={hintId}
          // A scrolling region must be reachable with the keyboard.
          tabIndex={0}
          onScroll={(event) => {
            setScrollTop(event.currentTarget.scrollTop);
          }}
        >
          <div role="rowgroup" className={styles.processHead}>
            <div role="row" className={styles.processRow} aria-rowindex={1}>
              {COLUMNS.map((column) => (
                <div
                  key={column}
                  role="columnheader"
                  className={`${NUMERIC.has(column) ? styles.number : ''} ${column === 'pid' ? styles.pidCell : ''}`}
                  aria-sort={sort.key === column ? sort.direction : 'none'}
                >
                  <button
                    type="button"
                    className={styles.sort}
                    onClick={() => {
                      setSort((current) => ({
                        key: column,
                        direction:
                          current.key === column
                            ? current.direction === 'ascending'
                              ? 'descending'
                              : 'ascending'
                            : defaultDirection(column),
                      }));
                    }}
                  >
                    {t(`processes.${column}`)}
                    {sort.key === column && (
                      <span aria-hidden="true">{sort.direction === 'ascending' ? ' ↑' : ' ↓'}</span>
                    )}
                  </button>
                </div>
              ))}
            </div>
          </div>
          {rows.length === 0 ? (
            <p className={styles.muted}>{t('processes.empty')}</p>
          ) : (
            <div
              role="rowgroup"
              className={styles.processBody}
              style={{ height: rows.length * rowHeight }}
            >
              {rows.slice(shown.first, shown.last).map((process, offset) => {
                const index = shown.first + offset;
                return (
                  <div
                    key={process.pid}
                    role="row"
                    aria-rowindex={index + 2}
                    className={styles.processRow}
                    style={{ transform: `translateY(${String(index * rowHeight)}px)` }}
                  >
                    <div role="cell" className={styles.mono} title={process.name}>
                      {process.name}
                    </div>
                    <div role="cell" className={`${styles.number} ${styles.pidCell}`}>
                      {process.pid}
                    </div>
                    <div role="cell">{process.user}</div>
                    <div role="cell" className={styles.number}>
                      {process.cpuPercent === null ? '' : percent.format(process.cpuPercent / 100)}
                    </div>
                    <div role="cell" className={styles.number}>
                      {formatBytes(process.memoryBytes, locale)}
                    </div>
                  </div>
                );
              })}
            </div>
          )}
        </div>
      )}
      <p id={hintId} className={styles.note}>
        {t('processes.cpuHint')}
      </p>
    </div>
  );
}
