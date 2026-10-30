import {
  useEffect,
  useRef,
  useState,
  type CSSProperties,
  type KeyboardEvent,
  type ReactNode,
} from 'react';
import { cx } from './cx';
import styles from './DetailsPane.module.css';
import { useUiTexts } from './UiProvider';

/** The props of {@link DetailsPane}. */
export interface DetailsPaneProps {
  /** The accessible name of the pane. */
  label?: string;
  /**
   * Class names for the pane itself: its padding, border and background. Its width is
   * `var(--details-pane-width, <default>)`, so the width the user dragged to applies; a layout that puts the pane below
   * the content sets `width: auto` and `--details-handle: none` to hide the handle.
   */
  className?: string;
  /** The details. */
  children: ReactNode;
}

/** The narrowest width a user can drag the pane to, in pixels. */
const MIN_WIDTH = 200;
/** The width the content beside the pane keeps at least, in pixels. */
const CONTENT_MIN = 240;
/** One arrow key press, in pixels. */
const STEP = 16;

/** How narrow and how wide the pane may get with `room` pixels for it and the content beside it. */
function bounds(room: number) {
  return { min: MIN_WIDTH, max: Math.max(MIN_WIDTH, room - CONTENT_MIN) };
}

/**
 * The details pane on the right of a list or table (D394), with a handle on its left edge that changes its width:
 * drag it, or focus it and use the arrow keys (Home and End for the narrowest and widest); a double click goes back to
 * the default width. The width lasts while the pane is shown.
 *
 * @param props - The label, the class names and the details.
 * @returns The pane with its handle.
 */
export function DetailsPane({ label, className, children }: DetailsPaneProps) {
  const texts = useUiTexts();
  const paneRef = useRef<HTMLDivElement>(null);
  const drag = useRef<{ x: number; width: number } | null>(null);
  const [width, setWidth] = useState<number | null>(null);
  // The pane's width and the room beside it, for the separator's value: syncing with the layout, not deriving state.
  const [size, setSize] = useState({ width: MIN_WIDTH, room: 0 });
  useEffect(() => {
    const pane = paneRef.current;
    if (pane === null) {
      return;
    }
    const observer = new ResizeObserver(() => {
      setSize({ width: pane.offsetWidth, room: pane.parentElement?.clientWidth ?? 0 });
    });
    observer.observe(pane);
    if (pane.parentElement !== null) {
      observer.observe(pane.parentElement);
    }
    return () => {
      observer.disconnect();
    };
  }, []);

  const limits = () => bounds(paneRef.current?.parentElement?.clientWidth ?? 0);
  const resize = (next: number) => {
    const { min, max } = limits();
    setWidth(Math.round(Math.min(max, Math.max(min, next))));
  };
  const current = () => width ?? paneRef.current?.offsetWidth ?? MIN_WIDTH;

  const onKeyDown = (event: KeyboardEvent<HTMLDivElement>) => {
    // The pane is on the right, so the left arrow widens it.
    const next =
      event.key === 'ArrowLeft'
        ? current() + STEP
        : event.key === 'ArrowRight'
          ? current() - STEP
          : event.key === 'Home'
            ? limits().min
            : event.key === 'End'
              ? limits().max
              : null;
    if (next !== null) {
      event.preventDefault();
      resize(next);
    }
  };

  const { min, max } = bounds(size.room);
  return (
    <div
      ref={paneRef}
      className={styles.pane}
      style={
        width === null
          ? undefined
          : ({ '--details-pane-width': `${String(width)}px` } as CSSProperties)
      }
    >
      <div
        role="separator"
        aria-orientation="vertical"
        aria-label={texts.details.resize}
        aria-valuemin={min}
        aria-valuemax={max}
        aria-valuenow={width ?? size.width}
        tabIndex={0}
        className={styles.handle}
        onPointerDown={(event) => {
          event.currentTarget.setPointerCapture(event.pointerId);
          drag.current = { x: event.clientX, width: current() };
        }}
        onPointerMove={(event) => {
          if (drag.current !== null) {
            resize(drag.current.width + drag.current.x - event.clientX);
          }
        }}
        onPointerUp={() => {
          drag.current = null;
        }}
        onPointerCancel={() => {
          drag.current = null;
        }}
        onDoubleClick={() => {
          setWidth(null);
        }}
        onKeyDown={onKeyDown}
      />
      <aside className={cx(styles.body, className)} aria-label={label}>
        {children}
      </aside>
    </div>
  );
}
