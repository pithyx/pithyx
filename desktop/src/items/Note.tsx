import { useQueryClient } from '@tanstack/react-query';
import {
  useEffect,
  useRef,
  useState,
  type CSSProperties,
  type PointerEvent as ReactPointerEvent,
} from 'react';
import { useTranslation } from 'react-i18next';
import noteStyles from '../shell/StickyNote.module.css';
import { Button, IconButton } from '../ui/Button';
import { cx } from '../ui/cx';
import { Dialog } from '../ui/Dialog';
import { IconGripHorizontal, IconX } from '../ui/icons';
import { announce } from '../wm/announce';
import { cellOrigin, nearestCell, placeNote, type Cell, type GridSize } from './grid';
import styles from './ItemsLayer.module.css';
import { trackPointer } from './pointer';
import { changeItems, deleteItems, type DesktopItem } from './sync';

/** Text is saved this long after the last key press, and right away when the note loses focus. */
const SAVE_DELAY = 800;
export const MAX_NOTE_LENGTH = 4000;

/**
 * A sticky note on the desktop, under every window. It moves by its bar with the pointer or with the arrow keys on
 * its grip, and snaps to the icon grid. Text is kept locally while it is edited, so a refresh never overwrites typing.
 */
export function Note({
  note,
  cell,
  size,
  free,
  autoFocus = false,
}: {
  note: DesktopItem;
  /** Where the note shows now (moved in on small screens). */
  cell: Cell;
  size: GridSize;
  /** False on phones: notes flow below the icons and do not move. */
  free: boolean;
  /** Focus the text once the note is on screen, after the menu that created it gave focus back. */
  autoFocus?: boolean;
}) {
  const { t } = useTranslation();
  const client = useQueryClient();
  const element = useRef<HTMLElement>(null);
  const [draft, setDraft] = useState<string | null>(null);
  const [confirming, setConfirming] = useState(false);
  const timer = useRef<ReturnType<typeof setTimeout>>(undefined);
  const unsaved = useRef<string | null>(null);
  const textField = useRef<HTMLTextAreaElement>(null);
  useEffect(() => {
    if (!autoFocus) {
      return;
    }
    const frame = requestAnimationFrame(() => {
      textField.current?.focus();
    });
    return () => {
      cancelAnimationFrame(frame);
    };
  }, [autoFocus]);
  const text = draft ?? note.text ?? '';

  function save(value: string) {
    clearTimeout(timer.current);
    timer.current = undefined;
    unsaved.current = null;
    if (value !== note.text) {
      void changeItems(client, [{ id: note.id, text: value }]);
    }
  }

  // Text still waiting for its save goes out when the note goes away, for example when the desktop closes.
  useEffect(
    () => () => {
      if (timer.current !== undefined && unsaved.current !== null) {
        clearTimeout(timer.current);
        void changeItems(client, [{ id: note.id, text: unsaved.current }]);
      }
    },
    [client, note.id],
  );

  function moveTo(target: Cell) {
    const next = placeNote(target, size);
    if (next.column !== note.column || next.row !== note.row) {
      void changeItems(client, [{ id: note.id, column: next.column, row: next.row }]);
    }
  }

  function onBarPointerDown(event: ReactPointerEvent<HTMLElement>) {
    if (
      !free ||
      event.button !== 0 ||
      (event.target as Element).closest('button:not([data-grip])') !== null
    ) {
      return;
    }
    const target = element.current;
    if (target === null) {
      return;
    }
    const origin = { x: target.offsetLeft, y: target.offsetTop };
    trackPointer(event, {
      move: (delta) => {
        target.style.transform = `translate(${String(delta.x)}px, ${String(delta.y)}px)`;
        target.dataset.dragging = '';
      },
      end: (delta, moved) => {
        target.style.transform = '';
        delete target.dataset.dragging;
        if (moved) {
          moveTo(nearestCell({ x: origin.x + delta.x, y: origin.y + delta.y }));
        }
      },
    });
  }

  const origin = cellOrigin(cell);
  const style: CSSProperties | undefined = free ? { left: origin.x, top: origin.y } : undefined;

  return (
    <section
      ref={element}
      className={cx(noteStyles.note, styles.note)}
      style={style}
      aria-label={t('items.note')}
      data-note={note.id}
    >
      <header className={noteStyles.bar} onPointerDown={onBarPointerDown}>
        {free && (
          <button
            type="button"
            data-grip
            className={styles.grip}
            aria-label={t('items.moveNote')}
            aria-describedby={`note-grip-${String(note.id)}`}
            onKeyDown={(event) => {
              const step = {
                ArrowLeft: { column: -1, row: 0 },
                ArrowRight: { column: 1, row: 0 },
                ArrowUp: { column: 0, row: -1 },
                ArrowDown: { column: 0, row: 1 },
              }[event.key];
              if (step === undefined) {
                return;
              }
              event.preventDefault();
              const next = placeNote(
                { column: cell.column + step.column, row: cell.row + step.row },
                size,
              );
              moveTo(next);
              announce(t('items.noteMoved', { column: next.column + 1, row: next.row + 1 }));
            }}
          >
            <IconGripHorizontal size={16} stroke={2} />
            <span id={`note-grip-${String(note.id)}`} hidden>
              {t('items.moveNoteHint')}
            </span>
          </button>
        )}
        <IconButton
          label={t('items.removeNote')}
          size="sm"
          className={noteStyles.close}
          onPress={() => {
            if (text.trim() === '') {
              deleteItems(client, [note.id]);
            } else {
              setConfirming(true);
            }
          }}
        >
          <IconX size={15} stroke={2} />
        </IconButton>
      </header>
      <textarea
        ref={textField}
        className={noteStyles.text}
        value={text}
        maxLength={MAX_NOTE_LENGTH}
        aria-label={t('items.note')}
        onChange={(event) => {
          const value = event.target.value;
          setDraft(value);
          unsaved.current = value;
          clearTimeout(timer.current);
          timer.current = setTimeout(() => {
            save(value);
          }, SAVE_DELAY);
        }}
        onBlur={() => {
          if (draft !== null) {
            save(draft);
            setDraft(null);
          }
        }}
      />
      {confirming && (
        <Dialog
          isOpen
          isDismissable
          role="alertdialog"
          tone="danger"
          onOpenChange={setConfirming}
          title={t('items.removeNoteTitle')}
          actions={
            <>
              <Button
                variant="quiet"
                onPress={() => {
                  setConfirming(false);
                }}
              >
                {t('items.cancel')}
              </Button>
              <Button
                variant="danger"
                onPress={() => {
                  setConfirming(false);
                  clearTimeout(timer.current);
                  timer.current = undefined;
                  deleteItems(client, [note.id]);
                }}
              >
                {t('items.removeNoteConfirm')}
              </Button>
            </>
          }
        >
          {t('items.removeNoteBody')}
        </Dialog>
      )}
    </section>
  );
}
