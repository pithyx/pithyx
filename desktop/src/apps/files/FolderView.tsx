import { useVirtualizer } from '@tanstack/react-virtual';
import type { FileEntry, FileItems, Formatters, SortKey, SortOrder } from '@pithyx/sdk';
import {
  useEffect,
  useId,
  useLayoutEffect,
  useRef,
  useState,
  type DragEvent,
  type KeyboardEvent,
  type MouseEvent,
} from 'react';
import { useTranslation } from 'react-i18next';
import { cx } from '@pithyx/ui';
import { IconArrowDown, IconArrowUp, IconFile, IconFolder, IconLink } from '../../ui/icons';
import styles from './FilesApp.module.css';
import {
  isSelected,
  selectAll,
  selectOnly,
  selectRange,
  toItems,
  toggle,
  type Selection,
} from './selection';

/** Drag data of entries dragged inside the Files app. */
export const ENTRIES_TYPE = 'application/x-pithyx-files';

export interface FolderViewProps {
  entries: readonly FileEntry[];
  total: number;
  loading: boolean;
  hasMore: boolean;
  loadMore: () => void;
  sort: SortKey;
  order: SortOrder;
  onSort: (sort: SortKey) => void;
  selection: Selection;
  onSelect: (selection: Selection) => void;
  cut: ReadonlySet<string>;
  renaming: string | null;
  onRenamed: (name: string, newName: string | null) => void;
  onOpen: (entry: FileEntry) => void;
  onUp: () => void;
  onKey: (key: 'delete' | 'deletePermanent' | 'rename' | 'copy' | 'cut' | 'paste') => void;
  onMenu: (x: number, y: number, entry: FileEntry | null) => void;
  onDropFiles: (files: File[], folder: string | null) => void;
  /** What a drag of these entries carries, so another Files window knows where they come from. */
  dragData: (items: FileItems) => string;
  /** Entries dropped on a folder row, or on the list itself (null) from another folder. */
  onDropEntries: (data: string, folder: string | null, copy: boolean) => void;
  /** Called once the rows of a newly shown folder are on screen, for the navigation timing. */
  onShown: () => void;
  folderKey: string;
  /** The SDK's formatters, in the user's language and time zone. */
  format: Formatters;
  /** Rows with details, or tiles with large thumbnails. */
  layout: 'list' | 'grid';
  /** The URL of an entry's thumbnail once it is ready; null shows the icon. */
  thumbnail: (entry: FileEntry) => string | null;
  /** The entries on screen changed; thumbnails are asked for these only. */
  onVisible: (entries: FileEntry[]) => void;
  /** Entries that are neither dragged nor dropped on: the homes of every user, which are shares of their own. */
  fixed?: boolean;
}

const ROW_HEIGHT = 36;
const COARSE_ROW_HEIGHT = 52;
const PAGE_ROWS = 10;
const TILE_WIDTH = 136;
const TILE_HEIGHT = 156;

function rowHeight(): number {
  return matchMedia('(pointer: coarse)').matches ? COARSE_ROW_HEIGHT : ROW_HEIGHT;
}

/**
 * A folder as a virtualized grid: only the rows on screen exist, the scrollbar covers the whole folder, and pages
 * are fetched as the view nears the end of what is loaded. As a list each row is an entry; as tiles each row holds
 * as many entries as fit the width, and the arrow keys move across and down. Focus stays on the grid; the active
 * entry is its `aria-activedescendant` and is scrolled into view before it is announced.
 */
export function FolderView(props: FolderViewProps) {
  const { entries, total, selection, onSelect } = props;
  const { t } = useTranslation('files');
  const scroll = useRef<HTMLDivElement>(null);
  const id = useId();
  const [active, setActive] = useState(0);
  const [focused, setFocused] = useState(false);
  const [dropTarget, setDropTarget] = useState<string | null>(null);
  const count = Math.max(total, entries.length);
  const [width, setWidth] = useState(0);
  const tiles = props.layout === 'grid';
  const columns = tiles ? Math.max(1, Math.floor((width - 16) / TILE_WIDTH)) : 1;
  // Read once per render: the virtualizer asks for every row's estimate whenever the count changes.
  const height = tiles ? TILE_HEIGHT : rowHeight();

  // The number of tiles per row follows the width of the view: syncing with the DOM.
  useLayoutEffect(() => {
    const element = scroll.current;
    if (element === null) {
      return;
    }
    setWidth(element.clientWidth);
    const observer = new ResizeObserver(() => {
      setWidth(element.clientWidth);
    });
    observer.observe(element);
    return () => {
      observer.disconnect();
    };
  }, []);
  // The compiler skips this component; the virtualizer's functions are only used here, never passed to memoized children.
  // eslint-disable-next-line react-hooks/incompatible-library
  const virtualizer = useVirtualizer({
    count: Math.ceil(count / columns),
    getScrollElement: () => scroll.current,
    estimateSize: () => height,
    overscan: 12,
  });
  const items = virtualizer.getVirtualItems();
  const lastVisible = ((items.at(-1)?.index ?? 0) + 1) * columns - 1;
  const firstVisible = (items[0]?.index ?? 0) * columns;
  const names = entries.map((entry) => entry.name);
  const activeIndex = Math.min(active, Math.max(0, count - 1));
  const { format } = props;

  // Pages follow the scroll position: syncing with the virtualizer, not deriving state.
  const { hasMore, loading, loadMore } = props;
  useEffect(() => {
    if (hasMore && !loading && lastVisible >= entries.length - 20) {
      loadMore();
    }
  }, [hasMore, loading, loadMore, lastVisible, entries.length]);

  // Thumbnails follow what is on screen: syncing with the virtualizer, not deriving state.
  const { onVisible } = props;
  const visibleFrom = Math.max(0, firstVisible);
  const visibleTo = Math.min(entries.length, lastVisible + 1);
  useEffect(() => {
    if (visibleTo > visibleFrom) {
      onVisible(entries.slice(visibleFrom, visibleTo));
    }
  }, [entries, onVisible, visibleFrom, visibleTo]);

  const { onShown, folderKey } = props;
  const shownKey = useRef<string | null>(null);
  useLayoutEffect(() => {
    if (entries.length > 0 || (!loading && total === 0)) {
      if (shownKey.current !== folderKey) {
        shownKey.current = folderKey;
        onShown();
      }
    }
  }, [entries.length, folderKey, loading, onShown, total]);

  function moveTo(index: number, event: { shiftKey: boolean; ctrlKey: boolean; metaKey: boolean }) {
    const next = Math.max(0, Math.min(count - 1, index));
    setActive(next);
    virtualizer.scrollToIndex(Math.floor(next / columns), { align: 'auto' });
    const name = names[next];
    if (name === undefined) {
      return;
    }
    if (event.shiftKey) {
      onSelect(selectRange(selection, names, name));
    } else if (!(event.ctrlKey || event.metaKey)) {
      onSelect(selectOnly(name));
    }
  }

  function onKeyDown(event: KeyboardEvent<HTMLDivElement>) {
    if (props.renaming !== null) {
      return;
    }
    const entry = entries[activeIndex];
    const control = event.ctrlKey || event.metaKey;
    const pageRows =
      (Math.max(1, Math.floor((scroll.current?.clientHeight ?? 0) / height) - 1) || PAGE_ROWS) *
      columns;
    const handled = (() => {
      switch (event.key) {
        case 'ArrowDown':
          moveTo(activeIndex + columns, event);
          return true;
        case 'ArrowUp':
          if (event.altKey) {
            props.onUp();
          } else {
            moveTo(activeIndex - columns, event);
          }
          return true;
        case 'ArrowRight':
          if (!tiles) {
            return false;
          }
          moveTo(activeIndex + 1, event);
          return true;
        case 'ArrowLeft':
          if (!tiles) {
            return false;
          }
          moveTo(activeIndex - 1, event);
          return true;
        case 'PageDown':
          moveTo(activeIndex + pageRows, event);
          return true;
        case 'PageUp':
          moveTo(activeIndex - pageRows, event);
          return true;
        case 'Home':
          moveTo(0, event);
          return true;
        case 'End':
          moveTo(count - 1, event);
          return true;
        case 'Backspace':
          props.onUp();
          return true;
        case 'Enter':
          if (entry !== undefined) {
            props.onOpen(entry);
          }
          return true;
        case ' ':
          if (entry !== undefined) {
            onSelect(control ? toggle(selection, entry.name) : selectOnly(entry.name));
          }
          return true;
        case 'Delete':
          props.onKey(event.shiftKey ? 'deletePermanent' : 'delete');
          return true;
        case 'F2':
          props.onKey('rename');
          return true;
        case 'Escape':
          onSelect({ kind: 'some', names: new Set(), anchor: null });
          return true;
        case 'ContextMenu': {
          const row = scroll.current
            ?.querySelector(`[data-index="${String(activeIndex)}"]`)
            ?.getBoundingClientRect();
          props.onMenu(row?.left ?? 0, row?.bottom ?? 0, entry ?? null);
          return true;
        }
        default:
          break;
      }
      if (event.key === 'F10' && event.shiftKey) {
        const row = scroll.current
          ?.querySelector(`[data-index="${String(activeIndex)}"]`)
          ?.getBoundingClientRect();
        props.onMenu(row?.left ?? 0, row?.bottom ?? 0, entry ?? null);
        return true;
      }
      if (control) {
        switch (event.key.toLowerCase()) {
          case 'a':
            onSelect(selectAll());
            return true;
          case 'c':
            props.onKey('copy');
            return true;
          case 'x':
            props.onKey('cut');
            return true;
          case 'v':
            props.onKey('paste');
            return true;
          default:
            return false;
        }
      }
      return false;
    })();
    if (handled) {
      event.preventDefault();
      event.stopPropagation();
    }
  }

  function onRowClick(event: MouseEvent, index: number, entry: FileEntry) {
    setActive(index);
    if (event.shiftKey) {
      onSelect(selectRange(selection, names, entry.name, event.ctrlKey || event.metaKey));
    } else if (event.ctrlKey || event.metaKey) {
      onSelect(toggle(selection, entry.name));
    } else {
      onSelect(selectOnly(entry.name));
    }
  }

  function dragKind(event: DragEvent): 'files' | 'entries' | null {
    const types = event.dataTransfer.types;
    return types.includes(ENTRIES_TYPE) ? 'entries' : types.includes('Files') ? 'files' : null;
  }

  function onDragOver(event: DragEvent, folder: string | null) {
    const kind = dragKind(event);
    if (kind === null || props.fixed === true) {
      return;
    }
    event.preventDefault();
    event.stopPropagation();
    event.dataTransfer.dropEffect =
      kind === 'entries' && !(event.ctrlKey || event.altKey) ? 'move' : 'copy';
    setDropTarget(folder ?? '');
  }

  function onDrop(event: DragEvent, folder: string | null) {
    const kind = dragKind(event);
    setDropTarget(null);
    if (kind === null) {
      return;
    }
    event.preventDefault();
    event.stopPropagation();
    if (kind === 'files') {
      props.onDropFiles([...event.dataTransfer.files], folder);
    } else {
      props.onDropEntries(
        event.dataTransfer.getData(ENTRIES_TYPE),
        folder,
        event.ctrlKey || event.altKey,
      );
    }
  }

  const sortButton = (key: SortKey, label: string, className?: string) => (
    <div
      role="columnheader"
      className={className}
      aria-sort={props.sort === key ? (props.order === 'asc' ? 'ascending' : 'descending') : 'none'}
    >
      <button
        type="button"
        className={styles.sortButton}
        onClick={() => {
          props.onSort(key);
        }}
      >
        {label}
        {props.sort === key &&
          (props.order === 'asc' ? (
            <IconArrowUp size={14} stroke={1.75} aria-hidden />
          ) : (
            <IconArrowDown size={14} stroke={1.75} aria-hidden />
          ))}
      </button>
    </div>
  );

  const activeId = `${id}-row-${String(activeIndex)}`;

  /** What a row of the list and a tile of the grid share: selection, drag and drop, opening and the menu. */
  function entryProps(entry: FileEntry, index: number) {
    const selected = isSelected(selection, entry.name);
    const isFolder = entry.kind === 'folder';
    return {
      id: `${id}-row-${String(index)}`,
      'aria-selected': selected,
      'data-index': index,
      'data-active': index === activeIndex ? '' : undefined,
      'data-focused': focused ? '' : undefined,
      'data-cut': props.cut.has(entry.name) ? '' : undefined,
      'data-drop-target': dropTarget === entry.name ? '' : undefined,
      draggable: props.fixed !== true && props.renaming !== entry.name,
      onDragStart: (event: DragEvent) => {
        const dragged = selected ? selection : selectOnly(entry.name);
        if (!selected) {
          onSelect(dragged);
        }
        event.dataTransfer.setData(ENTRIES_TYPE, props.dragData(toItems(dragged)));
        event.dataTransfer.effectAllowed = 'copyMove';
      },
      onDragOver: isFolder
        ? (event: DragEvent) => {
            onDragOver(event, entry.name);
          }
        : undefined,
      onDragLeave: isFolder
        ? () => {
            setDropTarget(null);
          }
        : undefined,
      onDrop: isFolder
        ? (event: DragEvent) => {
            onDrop(event, entry.name);
          }
        : undefined,
      onClick: (event: MouseEvent) => {
        onRowClick(event, index, entry);
      },
      onDoubleClick: () => {
        props.onOpen(entry);
      },
      onContextMenu: (event: MouseEvent) => {
        event.preventDefault();
        setActive(index);
        if (!selected) {
          onSelect(selectOnly(entry.name));
        }
        props.onMenu(event.clientX, event.clientY, entry);
      },
    };
  }

  function label(entry: FileEntry) {
    return props.renaming === entry.name ? (
      <RenameInput
        name={entry.name}
        label={t('dialog.rename', { name: entry.name })}
        onDone={(value) => {
          props.onRenamed(entry.name, value);
        }}
      />
    ) : (
      <span>{entry.name}</span>
    );
  }

  function glyph(entry: FileEntry, size: number, className: string) {
    const url = entry.kind === 'file' ? props.thumbnail(entry) : null;
    if (url !== null) {
      return <img src={url} alt="" className={className} decoding="async" draggable={false} />;
    }
    const Glyph =
      entry.kind === 'folder' ? IconFolder : entry.kind === 'symlink' ? IconLink : IconFile;
    return (
      <Glyph
        size={size}
        stroke={1.5}
        className={entry.kind === 'folder' ? styles.folderIcon : styles.fileIcon}
        aria-hidden
      />
    );
  }

  return (
    <div
      ref={scroll}
      className={styles.scroll}
      data-drop-target={dropTarget === '' ? '' : undefined}
      onDragOver={(event) => {
        onDragOver(event, null);
      }}
      onDragLeave={(event) => {
        if (event.currentTarget === event.target) {
          setDropTarget(null);
        }
      }}
      onDrop={(event) => {
        onDrop(event, null);
      }}
      onContextMenu={(event) => {
        if (event.target === event.currentTarget) {
          event.preventDefault();
          props.onMenu(event.clientX, event.clientY, null);
        }
      }}
    >
      <div
        role="grid"
        tabIndex={0}
        aria-label={t('list')}
        aria-multiselectable
        aria-rowcount={Math.ceil(count / columns) + 1}
        aria-colcount={tiles ? columns : 3}
        aria-busy={props.loading && entries.length === 0}
        aria-activedescendant={entries[activeIndex] === undefined ? undefined : activeId}
        className={styles.grid}
        data-layout={props.layout}
        onKeyDown={onKeyDown}
        onFocus={() => {
          setFocused(true);
        }}
        onBlur={() => {
          setFocused(false);
        }}
        data-testid="folder-grid"
      >
        <div role="row" aria-rowindex={1} className={styles.header}>
          {sortButton('name', t('columns.name'))}
          {sortButton('size', t('columns.size'), styles.size)}
          {sortButton('modified', t('columns.modified'), styles.optional)}
        </div>
        <div role="rowgroup" style={{ position: 'relative', height: virtualizer.getTotalSize() }}>
          {items.map((item) => {
            const style = { transform: `translateY(${String(item.start)}px)` };
            if (tiles) {
              const first = item.index * columns;
              return (
                <div
                  key={item.key}
                  role="row"
                  aria-rowindex={item.index + 2}
                  className={styles.tileRow}
                  style={{
                    ...style,
                    gridTemplateColumns: `repeat(${String(columns)}, ${String(TILE_WIDTH)}px)`,
                  }}
                >
                  {Array.from({ length: Math.min(columns, count - first) }, (_, column) => {
                    const index = first + column;
                    const entry = entries[index];
                    if (entry === undefined) {
                      return (
                        <div key={index} role="gridcell" className={styles.tile} data-index={index}>
                          <div className={styles.placeholder} aria-label={t('loading')} />
                        </div>
                      );
                    }
                    return (
                      <div
                        key={entry.name}
                        role="gridcell"
                        aria-colindex={column + 1}
                        className={styles.tile}
                        title={entry.name}
                        {...entryProps(entry, index)}
                      >
                        <span className={styles.tileImage}>
                          {glyph(entry, 48, styles.tileThumb)}
                        </span>
                        <span className={styles.tileName}>{label(entry)}</span>
                      </div>
                    );
                  })}
                </div>
              );
            }
            const entry = entries[item.index];
            if (entry === undefined) {
              return (
                <div
                  key={item.key}
                  role="row"
                  aria-rowindex={item.index + 2}
                  className={styles.row}
                  style={style}
                  data-index={item.index}
                >
                  <div role="gridcell" className={styles.cell}>
                    <div className={styles.placeholder} aria-label={t('loading')} />
                  </div>
                </div>
              );
            }
            const isFolder = entry.kind === 'folder';
            return (
              <div
                key={entry.name}
                role="row"
                aria-rowindex={item.index + 2}
                className={styles.row}
                style={style}
                {...entryProps(entry, item.index)}
              >
                <div role="gridcell" className={styles.cell}>
                  <span className={styles.name}>
                    {glyph(entry, 18, styles.rowThumb)}
                    {label(entry)}
                  </span>
                </div>
                <div role="gridcell" className={cx(styles.cell, styles.size)}>
                  {isFolder ? '' : format.bytes(entry.size)}
                </div>
                <div role="gridcell" className={cx(styles.cell, styles.optional)}>
                  {entry.modified === '' ? '' : format.dateTime(entry.modified)}
                </div>
              </div>
            );
          })}
        </div>
      </div>
      {!props.loading && count === 0 && <p className={styles.empty}>{t('emptyFolder')}</p>}
    </div>
  );
}

function RenameInput({
  name,
  label,
  onDone,
}: {
  name: string;
  label: string;
  onDone: (value: string | null) => void;
}) {
  const input = useRef<HTMLInputElement>(null);
  // Focus and select the name without its extension, like other file managers: syncing with the DOM.
  useLayoutEffect(() => {
    const element = input.current;
    if (element !== null) {
      element.focus();
      const dot = name.lastIndexOf('.');
      element.setSelectionRange(0, dot > 0 ? dot : name.length);
    }
  }, [name]);
  return (
    <input
      ref={input}
      className={styles.renameInput}
      defaultValue={name}
      aria-label={label}
      onClick={(event) => {
        event.stopPropagation();
      }}
      onKeyDown={(event) => {
        event.stopPropagation();
        if (event.key === 'Enter') {
          onDone(event.currentTarget.value);
        } else if (event.key === 'Escape') {
          onDone(null);
        }
      }}
      onBlur={(event) => {
        onDone(event.currentTarget.value);
      }}
    />
  );
}
