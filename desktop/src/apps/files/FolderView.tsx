import { useVirtualizer } from '@tanstack/react-virtual';
import type { FileEntry, Formatters, SortKey, SortOrder } from '@vyxis/sdk';
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
import { cx } from '../../ui/cx';
import { IconArrowDown, IconArrowUp, IconFile, IconFolder, IconLink } from '../../ui/icons';
import styles from './FilesApp.module.css';
import {
  isSelected,
  selectAll,
  selectOnly,
  selectRange,
  toggle,
  type Selection,
} from './selection';

/** Drag data of entries dragged inside the Files app. */
export const ENTRIES_TYPE = 'application/x-vyxis-files';

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
  onDropEntries: (data: string, folder: string, copy: boolean) => void;
  /** Called once the rows of a newly shown folder are on screen, for the navigation timing. */
  onShown: () => void;
  folderKey: string;
  /** The SDK's formatters, in the user's language and time zone. */
  format: Formatters;
}

const ROW_HEIGHT = 36;
const COARSE_ROW_HEIGHT = 52;
const PAGE_ROWS = 10;

function rowHeight(): number {
  return matchMedia('(pointer: coarse)').matches ? COARSE_ROW_HEIGHT : ROW_HEIGHT;
}

/**
 * A folder as a virtualized grid: only the rows on screen exist, the scrollbar covers the whole folder, and pages
 * are fetched as the view nears the end of what is loaded. Focus stays on the grid; the active row is its
 * `aria-activedescendant` and is scrolled into view before it is announced.
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
  // The compiler skips this component; the virtualizer's functions are only used here, never passed to memoized children.
  // eslint-disable-next-line react-hooks/incompatible-library
  const virtualizer = useVirtualizer({
    count,
    getScrollElement: () => scroll.current,
    estimateSize: rowHeight,
    overscan: 12,
  });
  const items = virtualizer.getVirtualItems();
  const lastVisible = items.at(-1)?.index ?? 0;
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
    virtualizer.scrollToIndex(next, { align: 'auto' });
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
      Math.max(1, Math.floor((scroll.current?.clientHeight ?? 0) / rowHeight()) - 1) || PAGE_ROWS;
    const handled = (() => {
      switch (event.key) {
        case 'ArrowDown':
          moveTo(activeIndex + 1, event);
          return true;
        case 'ArrowUp':
          if (event.altKey) {
            props.onUp();
          } else {
            moveTo(activeIndex - 1, event);
          }
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
    if (kind === null || (kind === 'entries' && folder === null)) {
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
    } else if (folder !== null) {
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
        aria-rowcount={count + 1}
        aria-busy={props.loading && entries.length === 0}
        aria-activedescendant={entries[activeIndex] === undefined ? undefined : activeId}
        className={styles.grid}
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
            const entry = entries[item.index];
            const style = { transform: `translateY(${String(item.start)}px)` };
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
            const Glyph =
              entry.kind === 'folder' ? IconFolder : entry.kind === 'symlink' ? IconLink : IconFile;
            const selected = isSelected(selection, entry.name);
            const isFolder = entry.kind === 'folder';
            return (
              <div
                key={entry.name}
                id={`${id}-row-${String(item.index)}`}
                role="row"
                aria-rowindex={item.index + 2}
                aria-selected={selected}
                className={styles.row}
                style={style}
                data-index={item.index}
                data-active={item.index === activeIndex ? '' : undefined}
                data-focused={focused ? '' : undefined}
                data-cut={props.cut.has(entry.name) ? '' : undefined}
                data-drop-target={dropTarget === entry.name ? '' : undefined}
                draggable={props.renaming !== entry.name}
                onDragStart={(event) => {
                  const dragged = selected ? selection : selectOnly(entry.name);
                  if (!selected) {
                    onSelect(dragged);
                  }
                  event.dataTransfer.setData(
                    ENTRIES_TYPE,
                    JSON.stringify(
                      dragged.kind === 'all'
                        ? { all: true, except: [...dragged.except] }
                        : { names: [...dragged.names] },
                    ),
                  );
                  event.dataTransfer.effectAllowed = 'copyMove';
                }}
                onDragOver={
                  isFolder
                    ? (event) => {
                        onDragOver(event, entry.name);
                      }
                    : undefined
                }
                onDragLeave={
                  isFolder
                    ? () => {
                        setDropTarget(null);
                      }
                    : undefined
                }
                onDrop={
                  isFolder
                    ? (event) => {
                        onDrop(event, entry.name);
                      }
                    : undefined
                }
                onClick={(event) => {
                  onRowClick(event, item.index, entry);
                }}
                onDoubleClick={() => {
                  props.onOpen(entry);
                }}
                onContextMenu={(event) => {
                  event.preventDefault();
                  setActive(item.index);
                  if (!selected) {
                    onSelect(selectOnly(entry.name));
                  }
                  props.onMenu(event.clientX, event.clientY, entry);
                }}
              >
                <div role="gridcell" className={styles.cell}>
                  <span className={styles.name}>
                    <Glyph
                      size={18}
                      stroke={1.75}
                      className={isFolder ? styles.folderIcon : styles.fileIcon}
                      aria-hidden
                    />
                    {props.renaming === entry.name ? (
                      <RenameInput
                        name={entry.name}
                        label={t('dialog.rename', { name: entry.name })}
                        onDone={(value) => {
                          props.onRenamed(entry.name, value);
                        }}
                      />
                    ) : (
                      <span>{entry.name}</span>
                    )}
                  </span>
                </div>
                <div role="gridcell" className={cx(styles.cell, styles.size)}>
                  {isFolder ? '' : format.bytes(entry.size)}
                </div>
                <div role="gridcell" className={cx(styles.cell, styles.optional)}>
                  {format.dateTime(entry.modified)}
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
