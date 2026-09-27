import { Suspense, useEffect, useRef } from 'react';
import { findApp } from '../apps/registry';
import { useAppName } from '../apps/names';
import { WindowFrame, type MediumPlacement } from '../shell/Window';
import { mediumZone, zoneRect, type Edge } from './geometry';
import { startDrag, startResize } from './pointer';
import { useSizeClass } from './sizeClass';
import { visibleRect } from './state';
import { dispatch, useDesktop } from './store';
import type { Rect } from './types';
import { WindowMenu } from './WindowMenu';
import styles from './WindowLayer.module.css';

const EDGES: readonly Edge[] = ['n', 's', 'e', 'w', 'ne', 'nw', 'se', 'sw'];

function px(value: number): string {
  return `${String(value)}px`;
}

/** One window of the window manager: frame, placement from the store and the app's lazily loaded content. */
export function ManagedWindow({ id }: { id: string }) {
  const windowState = useDesktop((state) => state.windows[id]);
  const layer = useDesktop((state) => state.order.indexOf(id) + 1);
  const focused = useDesktop((state) => state.focused === id);
  const workspace = useDesktop((state) => state.workspace);
  const sizeClass = useSizeClass();
  const appName = useAppName();
  const frame = useRef<HTMLElement>(null);

  // Keyboard and screen reader users follow the focused window: move DOM focus into it unless it is already there.
  useEffect(() => {
    const element = frame.current;
    if (focused && element !== null && !element.contains(document.activeElement)) {
      element.focus({ preventScroll: true });
    }
  }, [focused]);

  const app = windowState === undefined ? undefined : findApp(windowState.app);
  if (windowState === undefined || app === undefined) {
    return null;
  }

  const { placement } = windowState;
  const title = windowState.title ?? appName(windowState.app);
  const expanded = sizeClass === 'expanded';
  const free = expanded && placement.kind === 'free';
  let rect: Rect = visibleRect(windowState, workspace);
  let medium: MediumPlacement = 'max';
  if (placement.kind === 'snapped') {
    rect = zoneRect(placement.zone, workspace);
    const half = mediumZone(placement.zone);
    medium = half === 'maximize' ? 'max' : half;
  }
  const request = () => {
    if (!focused) {
      dispatch({ type: 'focus', id });
    }
  };

  return (
    <WindowFrame
      frameRef={frame}
      app={app}
      title={title}
      focused={focused}
      maximized={placement.kind === 'maximized'}
      minimized={windowState.minimized}
      position={{ x: px(rect.x), y: px(rect.y), width: px(rect.width), height: px(rect.height) }}
      medium={medium}
      compactActive={focused && sizeClass === 'compact'}
      layer={layer}
      icon={<WindowMenu id={id} app={app} title={title} placement={placement} free={free} />}
      titlebarProps={
        expanded
          ? {
              className: styles.draggable,
              onPointerDown: (event) => {
                if (frame.current !== null) {
                  startDrag(event, id, frame.current);
                }
              },
              onDoubleClick: (event) => {
                if ((event.target as Element).closest('button') === null) {
                  dispatch({ type: 'toggleMaximize', id });
                }
              },
            }
          : undefined
      }
      decorations={
        free &&
        EDGES.map((edge) => (
          <div
            key={edge}
            className={styles.handle}
            data-edge={edge}
            aria-hidden
            onPointerDown={(event) => {
              if (frame.current !== null) {
                startResize(event, id, frame.current, edge);
              }
            }}
          />
        ))
      }
      onFocusRequest={request}
      onMinimize={() => {
        dispatch({ type: 'minimize', id });
      }}
      onToggleMaximize={() => {
        dispatch({ type: 'toggleMaximize', id });
      }}
      onClose={() => {
        dispatch({ type: 'close', id });
      }}
    >
      <Suspense fallback={null}>
        <app.Component windowId={id} />
      </Suspense>
    </WindowFrame>
  );
}
