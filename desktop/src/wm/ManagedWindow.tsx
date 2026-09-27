import { Suspense, useEffect, useRef } from 'react';
import { findApp } from '../apps/registry';
import { useAppName } from '../apps/names';
import { WindowFrame, type MediumPlacement } from '../shell/Window';
import { mediumZone, zoneRect } from './geometry';
import { useSizeClass } from './sizeClass';
import { dispatch, useDesktop } from './store';
import type { Rect } from './types';

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
  let rect: Rect = windowState.rect;
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
      title={windowState.title ?? appName(windowState.app)}
      focused={focused}
      maximized={placement.kind === 'maximized'}
      minimized={windowState.minimized}
      position={{ x: px(rect.x), y: px(rect.y), width: px(rect.width), height: px(rect.height) }}
      medium={medium}
      compactActive={focused && sizeClass === 'compact'}
      layer={layer}
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
