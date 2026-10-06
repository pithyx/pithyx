import { describe, expect, it } from 'vitest';
import {
  answerStep,
  badgeText,
  closeStep,
  isOnline,
  isWindowVisible,
  launchOf,
  pagePoint,
  sessionOf,
  withArgs,
} from './rules';

describe('window arguments', () => {
  it('reads launch arguments and saved state, with defaults for old layouts', () => {
    const launch = { args: { section: 'disks' } };
    expect(launchOf({ launch, session: { count: 2 } })).toEqual(launch);
    expect(sessionOf({ launch, session: { count: 2 } })).toEqual({ count: 2 });
    for (const old of [undefined, null, 'x', [1], { section: 'disks' }, { launch: 'x' }]) {
      expect(launchOf(old)).toEqual({ args: null });
      expect(sessionOf(old)).toBeNull();
    }
  });

  it('replaces one part and keeps the other', () => {
    const args = { launch: { args: 1 }, session: { count: 2 }, stray: true };
    expect(withArgs(args, { launch: { args: 3 } })).toEqual({
      launch: { args: 3 },
      session: { count: 2 },
    });
    expect(withArgs(args, { session: null })).toEqual({ launch: { args: 1 }, session: null });
    expect(withArgs(undefined, { session: 1 })).toEqual({ session: 1 });
  });
});

describe('visibility and connection', () => {
  it('counts a window as visible unless it is minimized or the tab is hidden', () => {
    expect(isWindowVisible(false, false)).toBe(true);
    expect(isWindowVisible(true, false)).toBe(false);
    expect(isWindowVisible(false, true)).toBe(false);
  });

  it('is offline only while the desktop reconnects or gave up', () => {
    expect(isOnline('idle')).toBe(true);
    expect(isOnline('connecting')).toBe(true);
    expect(isOnline('open')).toBe(true);
    expect(isOnline('reconnecting')).toBe(false);
    expect(isOnline('stopped')).toBe(false);
  });
});

describe('pagePoint', () => {
  const content = { left: 100, top: 50, width: 400, height: 300 };

  it('moves a point of the window content onto the page', () => {
    expect(pagePoint({ x: 10.4, y: 20.6 }, content)).toEqual({ x: 110, y: 71 });
  });

  it('keeps a menu inside the app window', () => {
    expect(pagePoint({ x: -50, y: 9000 }, content)).toEqual({ x: 100, y: 350 });
    expect(pagePoint({ x: 9000, y: -1 }, { ...content, width: -5 })).toEqual({ x: 100, y: 50 });
  });
});

describe('badgeText', () => {
  it('shows positive counts in the user locale and caps them', () => {
    expect(badgeText(3, 'en')).toBe('3');
    expect(badgeText(99, 'en')).toBe('99');
    expect(badgeText(100, 'en')).toBe('99+');
    expect(badgeText(5, 'ar-EG')).toBe('٥');
  });

  it('shows nothing for zero, null or no count', () => {
    expect(badgeText(0, 'en')).toBeNull();
    expect(badgeText(null, 'en')).toBeNull();
    expect(badgeText(undefined, 'en')).toBeNull();
  });
});

describe('closing', () => {
  it('asks the app unless it marked the window clean', () => {
    expect(closeStep(false)).toBe('close');
    expect(closeStep(true)).toBe('ask');
    expect(closeStep(undefined)).toBe('ask');
  });

  it('offers to close anyway when the app does not answer in time (D62)', () => {
    expect(answerStep({ allow: true })).toBe('close');
    expect(answerStep({ allow: false })).toBe('keep');
    expect(answerStep(undefined)).toBe('offer');
  });
});
