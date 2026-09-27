import { describe, expect, it } from 'vitest';
import { MIN_VISIBLE_START } from './geometry';
import { APP_DATA_LIMIT, LAYOUT_VERSION, fromLayout, toLayout } from './layout';
import { initialState, reduce, visibleRect } from './state';
import type { Action, DesktopState, WindowOptions } from './types';

const workspace = { width: 1440, height: 848 };
const options: WindowOptions = {
  instances: 'multi',
  defaultSize: { width: 800, height: 500 },
  minSize: { width: 320, height: 200 },
};
const known = (app: string) => (app === 'gone' ? undefined : options);

function run(...actions: Action[]): DesktopState {
  return actions.reduce(reduce, initialState(workspace));
}

describe('toLayout and fromLayout', () => {
  it('round-trips windows, order, focus and the taskbar position', () => {
    const state = run(
      { type: 'open', app: 'disks', options },
      { type: 'open', app: 'about', options, args: { tab: 'licenses' } },
      { type: 'open', app: 'disks', options },
      { type: 'snap', id: 'w1', zone: 'left' },
      { type: 'minimize', id: 'w3' },
      { type: 'setTaskbar', position: 'left' },
    );
    const layout = toLayout(state);
    expect(layout.version).toBe(LAYOUT_VERSION);
    const restored = fromLayout(JSON.parse(JSON.stringify(layout)), known, workspace);
    expect(restored.skipped).toEqual([]);
    expect(restored.state).toEqual(state);
  });

  it('skips windows of apps that are gone and keeps the rest in order', () => {
    const state = run(
      { type: 'open', app: 'disks', options },
      { type: 'open', app: 'gone', options },
      { type: 'open', app: 'about', options },
    );
    const restored = fromLayout(toLayout(state), known, workspace);
    expect(restored.skipped).toEqual(['gone']);
    expect(restored.state.order).toEqual(['w1', 'w3']);
    expect(restored.state.focused).toBe('w3');
    expect(restored.state.nextId).toBe(4);
  });

  it('keeps geometry from a larger screen and shows it within the current workspace', () => {
    const wide = { ...initialState({ width: 3440, height: 1388 }) };
    const state = [
      { type: 'open', app: 'disks', options } as const,
      { type: 'move', id: 'w1', rect: { x: 3000, y: 1300, width: 700, height: 500 } } as const,
    ].reduce<DesktopState>(reduce, wide);
    const restored = fromLayout(toLayout(state), known, workspace);
    const window = restored.state.windows.w1;
    expect(window?.rect.x).toBe(3000);
    expect(window && visibleRect(window, workspace).x).toBe(workspace.width - MIN_VISIBLE_START);
    expect(restored.state.workspace).toEqual(workspace);
  });

  it('drops arguments that do not fit the budget, keeping the top-most windows first', () => {
    const big = 'x'.repeat(APP_DATA_LIMIT / 2);
    const state = run(
      { type: 'open', app: 'a', options, args: big },
      { type: 'open', app: 'a', options, args: big },
      { type: 'open', app: 'a', options, args: { small: true } },
    );
    const layout = toLayout(state);
    const args = Object.fromEntries(layout.windows.map((w) => [w.id, w.args]));
    expect(args.w3).toEqual({ small: true });
    expect(args.w2).toBe(big);
    expect(args.w1).toBeNull();
  });

  it('survives garbage without throwing', () => {
    for (const input of [
      null,
      42,
      'layout',
      {},
      { version: 2, windows: [] },
      { version: 1, windows: 'nope' },
      {
        version: 1,
        taskbar: 'diagonal',
        windows: [
          null,
          { id: 'w1' },
          { id: 'w2', app: 'disks', rect: { x: 0, y: 0, width: -5, height: 10 } },
          { id: 'w3', app: 'disks', rect: { x: 'a', y: 0, width: 5, height: 10 } },
          {
            id: 'w4',
            app: 'disks',
            rect: { x: 10, y: 10, width: 400, height: 300 },
            placement: { kind: 'snapped', zone: 'upside-down' },
          },
          { id: 'w4', app: 'disks', rect: { x: 10, y: 10, width: 400, height: 300 } },
        ],
        order: ['w4', 'w9', 7],
        focused: 'w9',
      },
    ]) {
      const restored = fromLayout(input, known, workspace);
      expect(restored.state.workspace).toEqual(workspace);
    }
    const restored = fromLayout(
      {
        version: 1,
        taskbar: 'diagonal',
        windows: [
          {
            id: 'w4',
            app: 'disks',
            rect: { x: 10, y: 10, width: 400, height: 300 },
            placement: 'x',
          },
        ],
        order: ['w4', 'w9'],
        focused: 'w9',
      },
      known,
      workspace,
    );
    expect(restored.state.taskbar).toBe('bottom');
    expect(restored.state.order).toEqual(['w4']);
    expect(restored.state.focused).toBe('w4');
    expect(restored.state.windows.w4?.placement).toEqual({ kind: 'free' });
    expect(restored.state.windows.w4?.minSize).toEqual(options.minSize);
  });

  it('restores at most 50 windows', () => {
    const windows = Array.from({ length: 80 }, (_, index) => ({
      id: `w${String(index + 1)}`,
      app: 'disks',
      rect: { x: 10, y: 10, width: 400, height: 300 },
    }));
    const restored = fromLayout(
      { version: 1, windows, order: [], focused: null },
      known,
      workspace,
    );
    expect(Object.keys(restored.state.windows)).toHaveLength(50);
    expect(restored.state.nextId).toBe(51);
  });
});
