import { IconChevronDown, IconChevronUp } from './icons';
import { useState, type CSSProperties, type ReactNode } from 'react';
import {
  Cell,
  Column,
  Row,
  Table,
  TableBody,
  TableHeader,
  TableLayout,
  Virtualizer,
  useLocale,
  type Key,
  type Selection,
  type SortDescriptor,
} from 'react-aria-components';
import { cx } from './cx';
import styles from './DataTable.module.css';

/** A column of a {@link DataTable}. */
export interface DataTableColumn<T> {
  /** A stable id, also the key of a sort. */
  id: string;
  /** The heading. */
  label: string;
  /** The cell of an item in this column. */
  cell: (item: T) => ReactNode;
  /** The value the column sorts by; the column is sortable when it is set. */
  sortValue?: (item: T) => string | number;
  /** `end` for numbers, which also get tabular figures; `start` when left out. */
  align?: 'start' | 'end';
  /** Whether the column is secondary and shown in the muted text color. */
  muted?: boolean;
  /** A fixed width in pixels such as `120`, or a share such as `'20%'`; the columns without one share the rest. */
  width?: number | `${number}%`;
  /** Whether the column names the row for screen readers; the first column when no column says so. */
  isRowHeader?: boolean;
}

/** The props of {@link DataTable}. */
export interface DataTableProps<T> {
  /** The accessible name of the table. */
  'aria-label': string;
  /** The columns in order. */
  columns: readonly DataTableColumn<T>[];
  /** The rows. */
  items: readonly T[];
  /** The key of an item, stable across renders. */
  getKey: (item: T) => Key;
  /** `none` (the default), `single` or `multiple`. A click selects, Ctrl or Shift extend, a long press on touch. */
  selectionMode?: 'none' | 'single' | 'multiple';
  /** The selected keys, for a controlled table. */
  selectedKeys?: Selection;
  /** The keys selected first, for an uncontrolled table. */
  defaultSelectedKeys?: Selection;
  /** Called when the selection changes. */
  onSelectionChange?: (keys: Selection) => void;
  /** Called on a double click or Enter on a row, for example to open it. */
  onRowAction?: (key: Key) => void;
  /**
   * The sort, for a controlled table. Without `onSortChange` the table sorts by the columns' `sortValue` itself,
   * starting with `defaultSort`.
   */
  sortDescriptor?: SortDescriptor;
  /** Called when the user sorts; the caller then sorts `items` itself. */
  onSortChange?: (descriptor: SortDescriptor) => void;
  /** The sort to start with when the table sorts itself. */
  defaultSort?: SortDescriptor;
  /** Shown in the body while there are no rows: one sentence about what is missing and the primary action. */
  emptyState?: ReactNode;
  /** `compact` rows (the default, 28 px) or `comfortable` ones (42 px) for two lines; 44 px on touch either way. */
  density?: 'compact' | 'comfortable';
  /**
   * Renders only the visible rows, for long lists. The table then needs a bounded height, for example the content of
   * an `AppFrame` with `scroll="content"`.
   */
  virtualized?: boolean;
  /** The details of the selected item, shown in a pane on the right; leave it out while the pane is switched off. */
  details?: ReactNode;
  /** The accessible name of the details pane. */
  detailsLabel?: string;
  /** Extra class names, added after the table's own. */
  className?: string;
}

const ROW_HEIGHT = { compact: 28, comfortable: 42, touch: 44 } as const;

function isCoarsePointer(): boolean {
  return typeof matchMedia === 'function' && matchMedia('(pointer: coarse)').matches;
}

/**
 * A table of items with more than two attributes (D394): a header row with sortable headings, compact rows, numbers
 * aligned to the end, the selection in the selection color with an accent outline, full keyboard support and an
 * optional details pane on the right. Long lists are virtualized with `virtualized`.
 *
 * @param props - The columns, the items and their keys, plus selection, sort, empty state, density and details.
 * @returns The table, with the details pane when given.
 *
 * @example
 * ```tsx
 * <DataTable
 *   aria-label="Users"
 *   columns={[
 *     { id: 'name', label: 'Name', cell: (user) => user.name, sortValue: (user) => user.name },
 *     { id: 'state', label: 'State', cell: (user) => <StatusBadge tone="success">Active</StatusBadge> },
 *   ]}
 *   items={users}
 *   getKey={(user) => user.id}
 *   selectionMode="single"
 *   onSelectionChange={setSelected}
 *   details={selectedUser && <UserDetails user={selectedUser} />}
 *   detailsLabel="Details"
 * />
 * ```
 */
export function DataTable<T>({
  'aria-label': label,
  columns,
  items,
  getKey,
  selectionMode = 'none',
  selectedKeys,
  defaultSelectedKeys,
  onSelectionChange,
  onRowAction,
  sortDescriptor,
  onSortChange,
  defaultSort,
  emptyState,
  density = 'compact',
  virtualized = false,
  details,
  detailsLabel,
  className,
}: DataTableProps<T>) {
  const { locale } = useLocale();
  const [ownSort, setOwnSort] = useState<SortDescriptor | undefined>(defaultSort);
  const sort = onSortChange === undefined ? ownSort : sortDescriptor;
  const rows = onSortChange === undefined ? sorted(items, columns, sort, locale) : items;
  const rowHeader = columns.find((column) => column.isRowHeader === true) ?? columns[0];

  const table = (
    <Table
      aria-label={label}
      className={cx(styles.table, styles[density], className)}
      style={{ '--columns': columns.length } as CSSProperties}
      selectionMode={selectionMode}
      selectionBehavior="replace"
      {...(selectedKeys === undefined ? {} : { selectedKeys })}
      {...(defaultSelectedKeys === undefined ? {} : { defaultSelectedKeys })}
      {...(onSelectionChange === undefined ? {} : { onSelectionChange })}
      {...(onRowAction === undefined ? {} : { onRowAction })}
      {...(sort === undefined ? {} : { sortDescriptor: sort })}
      onSortChange={onSortChange ?? setOwnSort}
    >
      <TableHeader className={styles.header}>
        {columns.map((column) => (
          <Column
            key={column.id}
            id={column.id}
            isRowHeader={column === rowHeader}
            allowsSorting={column.sortValue !== undefined}
            className={cx(styles.column, column.align === 'end' && styles.end)}
            {...(column.width === undefined
              ? {}
              : virtualized
                ? { width: column.width }
                : { style: { width: column.width } })}
          >
            {({ allowsSorting, sortDirection }) => (
              <span className={styles.heading}>
                {column.label}
                {allowsSorting && sortDirection !== undefined && (
                  <span className={styles.sortIcon} aria-hidden>
                    {sortDirection === 'ascending' ? (
                      <IconChevronUp size={14} stroke={2} />
                    ) : (
                      <IconChevronDown size={14} stroke={2} />
                    )}
                  </span>
                )}
              </span>
            )}
          </Column>
        ))}
      </TableHeader>
      <TableBody
        items={rows.map((item) => ({ key: getKey(item), item }))}
        renderEmptyState={() =>
          emptyState === undefined ? null : <div className={styles.empty}>{emptyState}</div>
        }
      >
        {({ key, item }) => (
          <Row id={key} className={styles.row}>
            {columns.map((column) => (
              <Cell
                key={column.id}
                className={cx(
                  styles.cell,
                  column.align === 'end' && styles.end,
                  column.muted === true && styles.muted,
                )}
              >
                {column.cell(item)}
              </Cell>
            ))}
          </Row>
        )}
      </TableBody>
    </Table>
  );

  const height = isCoarsePointer() ? ROW_HEIGHT.touch : ROW_HEIGHT[density];
  return (
    <div className={styles.container}>
      <div className={styles.view}>
        <div className={cx(styles.scroll, virtualized && styles.virtual)}>
          {virtualized ? (
            <Virtualizer
              layout={TableLayout}
              layoutOptions={{ rowHeight: height, headingHeight: 30 }}
            >
              {table}
            </Virtualizer>
          ) : (
            table
          )}
        </div>
        {details !== undefined && (
          <aside className={styles.details} aria-label={detailsLabel}>
            {details}
          </aside>
        )}
      </div>
    </div>
  );
}

function sorted<T>(
  items: readonly T[],
  columns: readonly DataTableColumn<T>[],
  sort: SortDescriptor | undefined,
  locale: string,
): readonly T[] {
  const value = columns.find((column) => column.id === sort?.column)?.sortValue;
  if (sort === undefined || value === undefined) {
    return items;
  }
  const collator = new Intl.Collator(locale, { numeric: true, sensitivity: 'base' });
  const direction = sort.direction === 'descending' ? -1 : 1;
  return [...items].sort((a, b) => {
    const left = value(a);
    const right = value(b);
    const order =
      typeof left === 'number' && typeof right === 'number'
        ? left - right
        : collator.compare(String(left), String(right));
    return order * direction;
  });
}
