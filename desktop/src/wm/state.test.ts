import { describe, expect, it } from 'vitest';
import { CASCADE_STEP, MIN_VISIBLE_END, MIN_VISIBLE_START, TITLEBAR_HEIGHT } from './geometry';
import { MAX_WINDOWS, canOpen, initialState, reduce, topVisible, visibleRect } from './state';
import type { Action, DesktopState, WindowOptions } from './types';

const workspace = { width: 1440, height: 848 };
const single: WindowOptions = {
  instances: 'single',
  defaultSize: { width: 800, height: 500 },
  minSize: { width: 320, height: 200 },
};
const multi: WindowOptions = { ...single, instances: 'multi' };

function run(...actions: Action[]): DesktopState {
  return actions.reduce(reduce, initialState(workspace));
}

function openMany(count: number): DesktopState {
  return run(
    ...Array.from(
      { length: count },
      () => ({ type: 'open', app: 'notes', options: multi }) as const,
    ),
  );
}

describe('open', () => {
  it('centers the first window and cascades the next ones', () => {
    const state = run(
      { type: 'open', app: 'a', options: multi },
      { type: 'open', app: 'a', options: multi },
    );
    const [first, second] = Object.values(state.windows);
    expect(first?.rect).toEqual({ x: 320, y: 116, width: 800, height: 500 });
    expect(second?.rect).toEqual({
      x: 320 + CASCADE_STEP,
      y: 116 + CASCADE_STEP,
      width: 800,
      height: 500,
    });
    expect(state.order).toEqual(['w1', 'w2']);
    expect(state.focused).toBe('w2');
  });

  it('wraps the cascade to the top left before a window leaves the workspace', () => {
    const state = openMany(12);
    for (const window of Object.values(state.windows)) {
      expect(window.rect.x + window.rect.width).toBeLessThanOrEqual(workspace.width);
      expect(window.rect.y + window.rect.height).toBeLessThanOrEqual(workspace.height);
    }
    // The eighth window ends at y 340 + 500; the ninth would pass the bottom edge.
    expect(state.windows.w8?.rect.y).toBe(116 + 7 * CASCADE_STEP);
    expect(state.windows.w9?.rect).toMatchObject({ x: CASCADE_STEP, y: CASCADE_STEP });
  });

  it('focuses the existing window of a single-instance app and passes the new arguments', () => {
    const state = run(
      { type: 'open', app: 'disks', options: single },
      { type: 'open', app: 'about', options: single },
      { type: 'minimize', id: 'w1' },
      { type: 'open', app: 'disks', options: single, args: { disk: 'sdb' } },
    );
    expect(Object.keys(state.windows)).toEqual(['w1', 'w2']);
    expect(state.focused).toBe('w1');
    expect(state.order).toEqual(['w2', 'w1']);
    expect(state.windows.w1?.minimized).toBe(false);
    expect(state.windows.w1?.args).toEqual({ disk: 'sdb' });
  });

  it('uses the remembered size within the minimum size and the workspace', () => {
    const state = run({
      type: 'open',
      app: 'a',
      options: multi,
      size: { width: 5000, height: 100 },
    });
    expect(state.windows.w1?.rect.width).toBe(workspace.width);
    expect(state.windows.w1?.rect.height).toBe(200);
  });

  it('refuses more than the maximum number of windows', () => {
    const full = openMany(MAX_WINDOWS);
    expect(canOpen(full, 'notes', 'multi')).toBe(false);
    expect(reduce(full, { type: 'open', app: 'notes', options: multi })).toBe(full);
    expect(canOpen(full, 'notes', 'single')).toBe(true);
  });
});

describe('focus and z-order', () => {
  it('raises a window to the top without reordering the taskbar', () => {
    const state = reduce(openMany(3), { type: 'focus', id: 'w1' });
    expect(state.order).toEqual(['w2', 'w3', 'w1']);
    expect(Object.keys(state.windows)).toEqual(['w1', 'w2', 'w3']);
    expect(state.focused).toBe('w1');
  });

  it('keeps the same state object when the focused window is focused again', () => {
    const state = openMany(2);
    expect(reduce(state, { type: 'focus', id: 'w2' }).order).toBe(state.order);
  });

  it('keeps a consistent order with 25 windows and random focus changes', () => {
    let state = openMany(25);
    for (const id of ['w3', 'w17', 'w3', 'w25', 'w1', 'w9']) {
      state = reduce(state, { type: 'focus', id });
    }
    expect(state.order).toHaveLength(25);
    expect(new Set(state.order).size).toBe(25);
    expect(state.order.slice(-4)).toEqual(['w3', 'w25', 'w1', 'w9']);
    expect(state.focused).toBe('w9');
  });

  it('ignores unknown windows', () => {
    const state = openMany(1);
    expect(reduce(state, { type: 'focus', id: 'nope' })).toBe(state);
    expect(reduce(state, { type: 'close', id: 'nope' })).toBe(state);
  });
});

describe('close and minimize', () => {
  it('moves focus to the top-most visible window', () => {
    const state = run(
      { type: 'open', app: 'a', options: multi },
      { type: 'open', app: 'a', options: multi },
      { type: 'open', app: 'a', options: multi },
      { type: 'minimize', id: 'w2' },
      { type: 'close', id: 'w3' },
    );
    expect(state.focused).toBe('w1');
    expect(state.order).toEqual(['w1', 'w2']);
  });

  it('leaves nothing focused when every window is minimized', () => {
    const state = run({ type: 'open', app: 'a', options: multi }, { type: 'minimize', id: 'w1' });
    expect(state.focused).toBeNull();
    expect(topVisible(state)).toBeNull();
  });

  it('does not steal focus when a background window is minimized or closed', () => {
    const minimized = reduce(openMany(3), { type: 'minimize', id: 'w1' });
    expect(minimized.focused).toBe('w3');
    expect(reduce(minimized, { type: 'close', id: 'w2' }).focused).toBe('w3');
  });

  it('closes everything', () => {
    const state = reduce(openMany(4), { type: 'closeAll' });
    expect(state.windows).toEqual({});
    expect(state.order).toEqual([]);
    expect(state.focused).toBeNull();
  });
});

describe('maximize, snap and restore', () => {
  it('keeps the free geometry while maximized and snapped', () => {
    const opened = openMany(1);
    const rect = opened.windows.w1?.rect;
    const maximized = reduce(opened, { type: 'toggleMaximize', id: 'w1' });
    expect(maximized.windows.w1?.placement).toEqual({ kind: 'maximized' });
    const snapped = reduce(maximized, { type: 'snap', id: 'w1', zone: 'left' });
    expect(snapped.windows.w1?.placement).toEqual({ kind: 'snapped', zone: 'left' });
    const restored = reduce(snapped, { type: 'toggleMaximize', id: 'w1' });
    expect(restored.windows.w1?.placement).toEqual({ kind: 'maximized' });
    const free = reduce(restored, { type: 'toggleMaximize', id: 'w1' });
    expect(free.windows.w1?.placement).toEqual({ kind: 'free' });
    expect(free.windows.w1?.rect).toEqual(rect);
  });

  it('raises and restores a minimized window that gets maximized', () => {
    const state = run(
      { type: 'open', app: 'a', options: multi },
      { type: 'open', app: 'a', options: multi },
      { type: 'minimize', id: 'w1' },
      { type: 'maximize', id: 'w1' },
    );
    expect(state.windows.w1?.minimized).toBe(false);
    expect(state.focused).toBe('w1');
  });
});

describe('move', () => {
  it('commits a free rect and leaves maximized or snapped placement', () => {
    const state = run(
      { type: 'open', app: 'a', options: multi },
      { type: 'maximize', id: 'w1' },
      { type: 'move', id: 'w1', rect: { x: 10, y: 20, width: 600, height: 400 } },
    );
    expect(state.windows.w1?.placement).toEqual({ kind: 'free' });
    expect(state.windows.w1?.rect).toEqual({ x: 10, y: 20, width: 600, height: 400 });
  });

  it('keeps the title bar reachable', () => {
    const state = run(
      { type: 'open', app: 'a', options: multi },
      { type: 'move', id: 'w1', rect: { x: -5000, y: -50, width: 600, height: 400 } },
    );
    expect(state.windows.w1?.rect).toEqual({
      x: MIN_VISIBLE_END - 600,
      y: 0,
      width: 600,
      height: 400,
    });
  });
});

describe('setGeometry', () => {
  it('restores rect and placement exactly, without clamping', () => {
    const state = run(
      { type: 'open', app: 'a', options: multi },
      { type: 'maximize', id: 'w1' },
      { type: 'restore', id: 'w1' },
      {
        type: 'setGeometry',
        id: 'w1',
        rect: { x: 5000, y: 5000, width: 600, height: 400 },
        placement: { kind: 'maximized' },
      },
    );
    expect(state.windows.w1?.rect).toEqual({ x: 5000, y: 5000, width: 600, height: 400 });
    expect(state.windows.w1?.placement).toEqual({ kind: 'maximized' });
  });
});

describe('workspace changes', () => {
  it('shows windows reachable in a smaller workspace without changing where they belong', () => {
    const wide = run(
      { type: 'setWorkspace', size: { width: 3440, height: 1388 } },
      { type: 'open', app: 'a', options: multi },
      { type: 'move', id: 'w1', rect: { x: 2800, y: 1200, width: 600, height: 400 } },
    );
    const small = reduce(wide, { type: 'setWorkspace', size: { width: 1280, height: 668 } });
    const window = small.windows.w1;
    if (window === undefined) {
      throw new Error('window missing');
    }
    expect(visibleRect(window, small.workspace)).toEqual({
      x: 1280 - MIN_VISIBLE_START,
      y: 668 - TITLEBAR_HEIGHT,
      width: 600,
      height: 400,
    });
    expect(window.rect).toEqual({ x: 2800, y: 1200, width: 600, height: 400 });
    const again = reduce(small, { type: 'setWorkspace', size: { width: 3440, height: 1388 } });
    expect(again.windows.w1).toBe(window);
  });

  it('shows windows larger than the workspace at the workspace size', () => {
    const state = run(
      { type: 'open', app: 'a', options: multi, size: { width: 1400, height: 800 } },
      { type: 'setWorkspace', size: { width: 390, height: 780 } },
    );
    const window = state.windows.w1;
    expect(window && visibleRect(window, state.workspace)).toMatchObject({
      width: 390,
      height: 780,
    });
  });

  it('cascades from where the previous window is shown', () => {
    const state = run(
      { type: 'open', app: 'a', options: multi },
      { type: 'move', id: 'w1', rect: { x: 1300, y: 600, width: 800, height: 500 } },
      { type: 'setWorkspace', size: { width: 1024, height: 700 } },
      { type: 'open', app: 'a', options: multi },
    );
    const second = state.windows.w2?.rect;
    expect((second?.x ?? 0) + (second?.width ?? 0)).toBeLessThanOrEqual(1024);
  });

  it('returns the same state when the size did not change', () => {
    const state = openMany(1);
    expect(reduce(state, { type: 'setWorkspace', size: { ...workspace } })).toBe(state);
  });
});

describe('cycle', () => {
  it('walks the taskbar order in both directions and wraps', () => {
    const state = openMany(3);
    const next = reduce(state, { type: 'cycle', direction: 1 });
    expect(next.focused).toBe('w1');
    expect(reduce(next, { type: 'cycle', direction: 1 }).focused).toBe('w2');
    expect(reduce(state, { type: 'cycle', direction: -1 }).focused).toBe('w2');
  });

  it('restores minimized windows on the way', () => {
    const state = run(
      { type: 'open', app: 'a', options: multi },
      { type: 'open', app: 'a', options: multi },
      { type: 'minimize', id: 'w1' },
      { type: 'cycle', direction: 1 },
    );
    expect(state.focused).toBe('w1');
    expect(state.windows.w1?.minimized).toBe(false);
  });

  it('does nothing without windows', () => {
    const state = initialState(workspace);
    expect(reduce(state, { type: 'cycle', direction: 1 })).toBe(state);
  });
});

describe('rapid changes', () => {
  it('stays consistent after many opens and closes', () => {
    let state = initialState(workspace);
    for (let round = 0; round < 200; round++) {
      state = reduce(state, { type: 'open', app: 'a', options: multi });
      if (round % 3 === 0) {
        state = reduce(state, { type: 'close', id: state.order[0] ?? '' });
      }
      if (round % 7 === 0) {
        state = reduce(state, { type: 'minimize', id: state.order.at(-1) ?? '' });
      }
    }
    const ids = Object.keys(state.windows);
    expect([...state.order].sort()).toEqual([...ids].sort());
    expect(state.focused === null || state.windows[state.focused]?.minimized === false).toBe(true);
    expect(state.focused).toBe(topVisible(state));
  });
});
