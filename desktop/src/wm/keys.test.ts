import { describe, expect, it } from 'vitest';
import { commandFor, nextSnapZone } from './keys';

// What Option+Space types on macOS.
const NO_BREAK_SPACE = String.fromCharCode(0xa0);

function key(
  key: string,
  modifiers: Partial<Record<'alt' | 'shift' | 'ctrl' | 'meta', boolean>> = {},
) {
  return {
    key,
    code: key === ' ' || key === NO_BREAK_SPACE ? 'Space' : key,
    altKey: modifiers.alt ?? true,
    shiftKey: modifiers.shift ?? true,
    ctrlKey: modifiers.ctrl ?? false,
    metaKey: modifiers.meta ?? false,
  };
}

describe('commandFor', () => {
  it('maps Alt+Shift shortcuts', () => {
    expect(commandFor(key('ArrowLeft'))).toEqual({ type: 'snap', side: 'left' });
    expect(commandFor(key('ArrowRight'))).toEqual({ type: 'snap', side: 'right' });
    expect(commandFor(key('ArrowUp'))).toEqual({ type: 'maximize' });
    expect(commandFor(key('ArrowDown'))).toEqual({ type: 'down' });
    expect(commandFor(key('PageDown'))).toEqual({ type: 'cycle', direction: 1 });
    expect(commandFor(key('PageUp'))).toEqual({ type: 'cycle', direction: -1 });
    expect(commandFor(key(' '))).toEqual({ type: 'menu' });
    expect(commandFor(key(NO_BREAK_SPACE))).toEqual({ type: 'menu' });
  });

  it('ignores other modifier combinations and keys', () => {
    expect(commandFor(key('ArrowLeft', { shift: false }))).toBeNull();
    expect(commandFor(key('ArrowLeft', { alt: false }))).toBeNull();
    expect(commandFor(key('ArrowLeft', { ctrl: true }))).toBeNull();
    expect(commandFor(key('ArrowLeft', { meta: true }))).toBeNull();
    expect(commandFor(key('w'))).toBeNull();
  });
});

describe('nextSnapZone', () => {
  it('always snaps to a half on normal screens', () => {
    expect(nextSnapZone({ kind: 'free' }, 'left', false)).toBe('left');
    expect(nextSnapZone({ kind: 'snapped', zone: 'left' }, 'left', false)).toBe('left');
  });

  it('cycles half, third and two thirds on ultrawide screens', () => {
    expect(nextSnapZone({ kind: 'free' }, 'left', true)).toBe('left');
    expect(nextSnapZone({ kind: 'snapped', zone: 'left' }, 'left', true)).toBe('left-third');
    expect(nextSnapZone({ kind: 'snapped', zone: 'left-third' }, 'left', true)).toBe(
      'left-two-thirds',
    );
    expect(nextSnapZone({ kind: 'snapped', zone: 'left-two-thirds' }, 'left', true)).toBe('left');
    expect(nextSnapZone({ kind: 'snapped', zone: 'left-third' }, 'right', true)).toBe('right');
  });
});
