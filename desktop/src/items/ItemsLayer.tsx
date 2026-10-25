import { useQueryClient, type QueryClient } from '@tanstack/react-query';
import {
  useEffect,
  useEffectEvent,
  useRef,
  useState,
  type KeyboardEvent,
  type PointerEvent as ReactPointerEvent,
} from 'react';
import { flushSync } from 'react-dom';
import { ListBox, ListBoxItem, type Selection } from 'react-aria-components';
import { useTranslation } from 'react-i18next';
import { useAppName } from '../apps/names';
import { findApp } from '../apps/registry';
import { DesktopItemFace } from '../shell/DesktopIcon';
import iconStyles from '../shell/DesktopIcon.module.css';
import { cx } from '@pithyx/ui';
import { announce } from '../wm/announce';
import { useSizeClass } from '../wm/sizeClass';
import { openApp, useDesktop } from '../wm/store';
import {
  CELL_HEIGHT,
  CELL_WIDTH,
  cellAt,
  cellOrigin,
  compareCells,
  firstFreeCell,
  gridSize,
  iconsInBox,
  moveIcons,
  neighbor,
  placeIcons,
  placeNote,
  type Box,
  type Cell,
  type Direction,
} from './grid';
import { ItemMenu, RenameDialog, type ItemAction } from './ItemMenus';
import styles from './ItemsLayer.module.css';
import { Note } from './Note';
import { trackPointer } from './pointer';
import { itemsKey } from './query';
import {
  changeItems,
  createItem,
  deleteItems,
  renderKey,
  useDesktopItems,
  type DesktopItem,
} from './sync';

const ARROWS: Record<string, Direction> = {
  ArrowUp: 'up',
  ArrowDown: 'down',
  ArrowLeft: 'left',
  ArrowRight: 'right',
};

const STEPS: Record<Direction, Cell> = {
  up: { column: 0, row: -1 },
  down: { column: 0, row: 1 },
  left: { column: -1, row: 0 },
  right: { column: 1, row: 0 },
};

/** How long a finger rests on an icon or the desktop before its menu opens. */
const LONG_PRESS = 500;

interface MenuState {
  x: number;
  y: number;
  kind: 'desktop' | 'items';
  /** Where a new item goes. */
  cell: Cell;
}

/** Keyboard move mode: arrow keys move the selected icons, Enter keeps the new place, Escape goes back. */
interface KeyboardMove {
  ids: number[];
  delta: Cell;
}

/** A shortcut whose app is gone (for example an uninstalled app) is kept on the daemon but not shown. */
function visibleShortcuts(items: readonly DesktopItem[]): DesktopItem[] {
  return items.filter(
    (item) => item.kind === 'shortcut' && item.app !== null && findApp(item.app) !== undefined,
  );
}

/** Puts a shortcut to an app in the first free cell of the visible grid, for the taskbar and start menu. */
export function addShortcut(client: QueryClient, app: string): void {
  const items = client.getQueryData<DesktopItem[]>(itemsKey) ?? [];
  const size = gridSize(useDesktop.getState().workspace);
  const cell = firstFreeCell(placeIcons(visibleShortcuts(items), size), size);
  createItem(client, { kind: 'shortcut', app, ...cell });
}

/**
 * The icons and sticky notes on the desktop, under every window. Icons sit on a grid and can be selected with a
 * click, Ctrl or Shift, a selection frame or the keyboard, moved by dragging or from the menu, renamed and removed.
 * On phones they flow in rows like a home screen.
 */
export function ItemsLayer() {
  const { t } = useTranslation();
  const client = useQueryClient();
  const appName = useAppName();
  const items = useDesktopItems().data ?? [];
  const sizeClass = useSizeClass();
  const free = sizeClass !== 'compact';
  const workspace = useDesktop((state) => state.workspace);
  const size = gridSize(workspace);
  const [selected, setSelected] = useState<ReadonlySet<number>>(new Set());
  const [menu, setMenu] = useState<MenuState | null>(null);
  const [renaming, setRenaming] = useState<DesktopItem | null>(null);
  const [keyboardMove, setKeyboardMoveState] = useState<KeyboardMove | null>(null);
  // Key presses can come faster than renders; each one builds on the latest move, not on the last rendered one.
  const latestMove = useRef<KeyboardMove | null>(null);
  function setKeyboardMove(move: KeyboardMove | null) {
    latestMove.current = move;
    setKeyboardMoveState(move);
  }
  const [frame, setFrame] = useState<Box | null>(null);
  // A note created from the menu takes focus once it is on screen.
  const [newNote, setNewNote] = useState<number | null>(null);
  const list = useRef<HTMLDivElement>(null);
  const anchorKey = useRef<number | null>(null);
  // Set while a drag ends, so the press that ends it neither changes the selection nor opens the app.
  const dragging = useRef(false);
  const longPress = useRef<ReturnType<typeof setTimeout>>(undefined);

  const shortcuts = visibleShortcuts(items);
  const notes = items.filter((item) => item.kind === 'note');
  const placed = placeIcons(shortcuts, size);
  const shown =
    keyboardMove === null
      ? placed
      : new Map([...placed, ...moveIcons(placed, keyboardMove.ids, keyboardMove.delta, size)]);
  const ordered = [...shortcuts].sort(
    (a, b) => compareCells(shown.get(a.id) ?? a, shown.get(b.id) ?? b) || a.id - b.id,
  );
  const labelOf = (item: DesktopItem) => item.name ?? appName(item.app ?? '');

  function open(id: number) {
    const item = shortcuts.find((entry) => entry.id === id);
    if (item?.app != null) {
      openApp(item.app);
    }
  }

  function select(ids: Iterable<number>) {
    setSelected(new Set(ids));
  }

  function commitMove(moved: ReadonlyMap<number, Cell>) {
    const changes = [...moved].flatMap(([id, cell]) => {
      const item = shortcuts.find((entry) => entry.id === id);
      return item === undefined || (item.column === cell.column && item.row === cell.row)
        ? []
        : [{ id, column: cell.column, row: cell.row }];
    });
    if (changes.length > 0) {
      void changeItems(client, changes);
    }
  }

  function removeSelected() {
    const ids = [...selected].filter((id) => shortcuts.some((item) => item.id === id));
    if (ids.length === 0) {
      return;
    }
    deleteItems(client, ids);
    announce(t('items.removed', { count: ids.length }));
    setSelected(new Set());
  }

  function workspacePoint(clientX: number, clientY: number) {
    const bounds = list.current?.getBoundingClientRect();
    return { x: clientX - (bounds?.left ?? 0), y: clientY - (bounds?.top ?? 0) };
  }

  function openMenu(clientX: number, clientY: number, key: number | null) {
    if (key !== null && !selected.has(key)) {
      select([key]);
    }
    if (key === null) {
      setSelected(new Set());
    }
    const kind = key === null ? 'desktop' : 'items';
    setMenu({ x: clientX, y: clientY, kind, cell: cellAt(workspacePoint(clientX, clientY)) });
  }

  function keyOf(target: EventTarget | null): number | null {
    const option = target instanceof Element ? target.closest('[role="option"]') : null;
    const key = option?.getAttribute('data-key');
    return key == null ? null : Number(key);
  }

  // The press that ends a drag or a long press is handled after the gesture; let it pass unnoticed.
  function endGesture() {
    clearTimeout(longPress.current);
    setTimeout(() => {
      dragging.current = false;
    });
  }

  function onPointerDown(event: ReactPointerEvent<HTMLDivElement>) {
    const inNote = event.target instanceof Element && event.target.closest('[data-note]') !== null;
    if (event.button !== 0 || keyboardMove !== null || inNote) {
      return;
    }
    const key = keyOf(event.target);
    if (event.pointerType === 'touch') {
      clearTimeout(longPress.current);
      const { clientX, clientY } = event;
      longPress.current = setTimeout(() => {
        dragging.current = true;
        openMenu(clientX, clientY, key);
      }, LONG_PRESS);
    }
    if (key !== null) {
      if (free) {
        dragIcons(event, key);
      } else {
        trackPointer(event, {
          move: () => {
            clearTimeout(longPress.current);
          },
          end: endGesture,
        });
      }
    } else if (event.target === list.current && free && event.pointerType !== 'touch') {
      selectWithFrame(event);
    } else if (event.target === list.current) {
      trackPointer(event, {
        move: () => {
          clearTimeout(longPress.current);
        },
        end: (_, moved) => {
          if (!moved && !dragging.current) {
            setSelected(new Set());
          }
          endGesture();
        },
      });
    }
  }

  function dragIcons(event: ReactPointerEvent<HTMLDivElement>, key: number) {
    const ids = selected.has(key) ? [...selected].filter((id) => placed.has(id)) : [key];
    const elements = ids.flatMap((id) => {
      const element = list.current?.querySelector<HTMLElement>(`[data-key="${String(id)}"]`);
      return element == null ? [] : [element];
    });
    trackPointer(event, {
      start: () => {
        clearTimeout(longPress.current);
        dragging.current = true;
        for (const element of elements) {
          element.dataset.dragging = '';
        }
      },
      move: (delta) => {
        for (const element of elements) {
          element.style.transform = `translate(${String(delta.x)}px, ${String(delta.y)}px)`;
        }
      },
      end: (delta, moved) => {
        if (moved) {
          const cells = {
            column: Math.round(delta.x / CELL_WIDTH),
            row: Math.round(delta.y / CELL_HEIGHT),
          };
          flushSync(() => {
            commitMove(moveIcons(placed, ids, cells, size));
            select(ids);
          });
        }
        for (const element of elements) {
          element.style.transform = '';
          delete element.dataset.dragging;
        }
        endGesture();
      },
    });
  }

  function selectWithFrame(event: ReactPointerEvent<HTMLDivElement>) {
    const origin = workspacePoint(event.clientX, event.clientY);
    const additive = event.ctrlKey || event.metaKey || event.shiftKey;
    const before = additive ? selected : new Set<number>();
    trackPointer(event, {
      move: (delta) => {
        const box = {
          x: Math.min(origin.x, origin.x + delta.x),
          y: Math.min(origin.y, origin.y + delta.y),
          width: Math.abs(delta.x),
          height: Math.abs(delta.y),
        };
        setFrame(box);
        select([...before, ...iconsInBox(placed, box)]);
      },
      end: (_, moved) => {
        setFrame(null);
        if (!moved && !additive) {
          setSelected(new Set());
        }
      },
    });
  }

  function focusIcon(id: number) {
    list.current?.querySelector<HTMLElement>(`[data-key="${String(id)}"]`)?.focus();
  }

  function focusedKey(): number | null {
    return keyOf(document.activeElement);
  }

  /** The keys of the keyboard move mode, wherever focus is while the item menu hands it back. */
  const onMoveKey = useEffectEvent((event: globalThis.KeyboardEvent) => {
    const direction = ARROWS[event.key];
    const move = latestMove.current;
    if (move !== null) {
      event.preventDefault();
      event.stopPropagation();
      if (direction !== undefined) {
        const step = STEPS[direction];
        setKeyboardMove({
          ids: move.ids,
          delta: {
            column: move.delta.column + step.column,
            row: move.delta.row + step.row,
          },
        });
      } else if (event.key === 'Enter') {
        commitMove(moveIcons(placed, move.ids, move.delta, size));
        setKeyboardMove(null);
        announce(t('items.moveDone'));
      } else if (event.key === 'Escape') {
        setKeyboardMove(null);
        announce(t('items.moveCancelled'));
      }
    }
  });

  // Listening on the window catches the first arrow key even before focus is back on the icon after the menu closed.
  const moving = keyboardMove !== null;
  useEffect(() => {
    if (!moving) {
      return;
    }
    const listener = (event: globalThis.KeyboardEvent) => {
      onMoveKey(event);
    };
    window.addEventListener('keydown', listener, true);
    return () => {
      window.removeEventListener('keydown', listener, true);
    };
  }, [moving]);

  function onKeyDownCapture(event: KeyboardEvent<HTMLDivElement>) {
    const direction = ARROWS[event.key];
    if (latestMove.current !== null) {
      return;
    }
    const from = focusedKey();
    if (direction === undefined || from === null || !free || event.altKey || event.metaKey) {
      return;
    }
    // The grid is two-dimensional and sparse, so the arrow keys go to the nearest icon on screen.
    event.preventDefault();
    event.stopPropagation();
    const next = neighbor(shown, from, direction);
    if (next === null) {
      return;
    }
    focusIcon(next);
    if (event.shiftKey) {
      const anchor = anchorKey.current ?? from;
      const start = ordered.findIndex((item) => item.id === anchor);
      const end = ordered.findIndex((item) => item.id === next);
      select(ordered.slice(Math.min(start, end), Math.max(start, end) + 1).map((item) => item.id));
    } else if (!event.ctrlKey) {
      anchorKey.current = next;
      select([next]);
    }
  }

  function onKeyDown(event: KeyboardEvent<HTMLDivElement>) {
    if (event.key === 'Delete') {
      event.preventDefault();
      removeSelected();
    } else if (event.key === 'F2' && selected.size === 1) {
      event.preventDefault();
      setRenaming(shortcuts.find((item) => selected.has(item.id)) ?? null);
    } else if (event.key === 'ContextMenu' || (event.key === 'F10' && event.shiftKey)) {
      event.preventDefault();
      const key = focusedKey();
      const rect = (key === null ? list.current : document.activeElement)?.getBoundingClientRect();
      openMenu(rect?.left ?? 0, rect?.bottom ?? 0, key);
    }
  }

  function create(kind: 'note' | 'shortcut', cell: Cell, app?: string) {
    if (kind === 'note') {
      setNewNote(createItem(client, { kind: 'note', text: '', ...placeNote(cell, size) }));
      return;
    }
    const taken = new Set(
      [...placed.values()].map((entry) => `${String(entry.column)}:${String(entry.row)}`),
    );
    const target =
      taken.has(`${String(cell.column)}:${String(cell.row)}`) ||
      cell.column >= size.columns ||
      cell.row >= size.rows
        ? firstFreeCell(placed, size)
        : cell;
    const id = createItem(client, { kind: 'shortcut', app: app ?? null, ...target });
    select([id]);
  }

  function onMenuAction(action: ItemAction) {
    const current = menu;
    setMenu(null);
    if (current === null) {
      return;
    }
    if (action === 'note') {
      create('note', current.cell);
    } else if (action.startsWith('shortcut:')) {
      create('shortcut', current.cell, action.slice('shortcut:'.length));
    } else if (action === 'open') {
      for (const id of selected) {
        open(id);
      }
    } else if (action === 'rename') {
      setRenaming(shortcuts.find((item) => selected.has(item.id)) ?? null);
    } else if (action === 'delete') {
      removeSelected();
    } else {
      const ids = [...selected].filter((id) => placed.has(id));
      setKeyboardMove({ ids, delta: { column: 0, row: 0 } });
      announce(t('items.moveStart', { count: ids.length }));
      const first = ids[0];
      if (first !== undefined) {
        requestAnimationFrame(() => {
          focusIcon(first);
        });
      }
    }
  }

  const single = selected.size === 1 ? shortcuts.find((item) => selected.has(item.id)) : undefined;

  return (
    <>
      <div
        className={styles.layer}
        // Capture: React Aria stops press events from bubbling out of the icons.
        onPointerDownCapture={onPointerDown}
        onPointerUp={() => {
          clearTimeout(longPress.current);
        }}
        onContextMenu={(event) => {
          // Notes have no menu of their own; their text keeps the browser's (spelling, paste), see Desktop.
          if (event.target instanceof Element && event.target.closest('[data-note]') !== null) {
            return;
          }
          event.preventDefault();
          if (!dragging.current) {
            openMenu(event.clientX, event.clientY, keyOf(event.target));
          }
        }}
        onKeyDownCapture={onKeyDownCapture}
        onKeyDown={onKeyDown}
      >
        <ListBox
          ref={list}
          aria-label={t('items.label')}
          layout="grid"
          selectionMode="multiple"
          selectionBehavior="replace"
          shouldSelectOnPressUp
          escapeKeyBehavior={keyboardMove === null ? 'clearSelection' : 'none'}
          selectedKeys={selected}
          onSelectionChange={(keys: Selection) => {
            if (dragging.current) {
              return;
            }
            const next = keys === 'all' ? ordered.map((item) => item.id) : [...keys].map(Number);
            if (next.length === 1) {
              anchorKey.current = next[0] ?? null;
            }
            select(next);
          }}
          onAction={(key) => {
            if (!dragging.current && keyboardMove === null) {
              open(Number(key));
            }
          }}
          className={cx(styles.icons, free ? styles.grid : styles.home)}
          items={ordered}
          dependencies={[shown, free]}
        >
          {(item) => {
            const cell = shown.get(item.id) ?? item;
            const origin = cellOrigin(cell);
            const name = labelOf(item);
            const app = findApp(item.app ?? '');
            return (
              <ListBoxItem
                id={item.id}
                textValue={name}
                aria-label={t('items.shortcut', { name })}
                className={cx(
                  iconStyles.icon,
                  styles.icon,
                  keyboardMove?.ids.includes(item.id) === true && styles.moving,
                )}
                {...(free ? { style: { left: origin.x, top: origin.y } } : {})}
              >
                {app !== undefined && (
                  <DesktopItemFace name={name} item={{ kind: 'shortcut', app }} />
                )}
              </ListBoxItem>
            );
          }}
        </ListBox>
        {frame !== null && (
          <div
            className={styles.frame}
            style={{ left: frame.x, top: frame.y, width: frame.width, height: frame.height }}
            aria-hidden
          />
        )}
        <div className={styles.notes}>
          {notes.map((note) => (
            <Note
              key={renderKey(note.id)}
              note={note}
              cell={placeNote(note, size)}
              size={size}
              free={free}
              autoFocus={renderKey(note.id) === newNote}
            />
          ))}
        </div>
      </div>
      {menu !== null && (
        <ItemMenu
          x={menu.x}
          y={menu.y}
          kind={menu.kind}
          count={selected.size}
          canRename={single !== undefined}
          onAction={onMenuAction}
          onClose={() => {
            setMenu(null);
          }}
        />
      )}
      {renaming !== null && (
        <RenameDialog
          name={renaming.name}
          appName={appName(renaming.app ?? '')}
          onRename={(name) => {
            void changeItems(client, [{ id: renaming.id, name }]);
            setRenaming(null);
          }}
          onClose={() => {
            setRenaming(null);
          }}
        />
      )}
    </>
  );
}
