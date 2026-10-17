import { describe, expect, it } from 'vitest';
import {
  answerStep,
  badgeText,
  closeStep,
  contextChanges,
  isOnline,
  isWindowVisible,
  launchOf,
  loadingPhase,
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

describe('daemon context changes', () => {
  const base = {
    permissions: ['notifications', 'files.read'],
    logLevel: 'info' as const,
    storage: [{ id: 'inbox', state: 'unmapped' as const, access: null }],
  };

  it('sends nothing while nothing changed, whatever the order of the permissions', () => {
    expect(contextChanges(base, { ...base, permissions: ['files.read', 'notifications'] })).toEqual(
      { events: [] },
    );
  });

  it('sends each changed part as its event', () => {
    const next = {
      permissions: ['files.read'],
      logLevel: 'debug' as const,
      storage: [{ id: 'inbox', state: 'ready' as const, access: 'read' as const }],
    };
    expect(contextChanges(base, next)).toEqual({
      permissions: ['files.read'],
      events: [
        { event: 'permissionsChange', data: { permissions: ['files.read'] } },
        { event: 'logLevelChange', data: { level: 'debug' } },
        {
          event: 'storageChange',
          data: { storage: [{ id: 'inbox', state: 'ready', access: 'read' }] },
        },
      ],
    });
  });

  it('tells the app when another version was installed, by an update or a rollback', () => {
    expect(contextChanges({ ...base, version: '1.0.0' }, { ...base, version: '1.0.1' })).toEqual({
      events: [{ event: 'appUpdated', data: { version: '1.0.1' } }],
    });
    expect(contextChanges({ ...base, version: '1.0.1' }, { ...base, version: '1.0.0' })).toEqual({
      events: [{ event: 'appUpdated', data: { version: '1.0.0' } }],
    });
    expect(contextChanges({ ...base, version: '1.0.0' }, { ...base, version: '1.0.0' })).toEqual({
      events: [],
    });
  });

  it('tells the app when its backend starts, runs or stops', () => {
    const stopped = { ...base, backend: { status: 'stopped' } };
    expect(contextChanges(stopped, { ...base, backend: { status: 'starting' } })).toEqual({
      events: [{ event: 'backendStatus', data: { status: 'starting' } }],
    });
    expect(contextChanges(stopped, stopped)).toEqual({ events: [] });
    expect(contextChanges({ ...base, backend: null }, { ...base, backend: null })).toEqual({
      events: [],
    });
  });

  it('tells the app why its backend cannot start and when that ends', () => {
    const waiting = { ...base, backend: { status: 'unavailable', reason: 'storage' } };
    expect(
      contextChanges({ ...base, backend: { status: 'stopped', reason: null } }, waiting),
    ).toEqual({
      events: [{ event: 'backendStatus', data: { status: 'unavailable', reason: 'storage' } }],
    });
    expect(
      contextChanges(waiting, { ...base, backend: { status: 'unavailable', reason: 'pool' } }),
    ).toEqual({
      events: [{ event: 'backendStatus', data: { status: 'unavailable', reason: 'pool' } }],
    });
    expect(contextChanges(waiting, { ...base, backend: { status: 'unavailable' } })).toEqual({
      events: [{ event: 'backendStatus', data: { status: 'unavailable' } }],
    });
  });
});

describe('loading placeholder', () => {
  const base = {
    connected: false,
    appReady: false,
    untilReady: false,
    timedOut: false,
    backendStatus: undefined,
  };

  it('stays until the app connected and names a starting backend meanwhile', () => {
    expect(loadingPhase(base)).toBe('loading');
    expect(loadingPhase({ ...base, backendStatus: 'stopped' })).toBe('loading');
    expect(loadingPhase({ ...base, backendStatus: 'starting' })).toBe('starting');
    expect(loadingPhase({ ...base, connected: true, backendStatus: 'starting' })).toBe('ready');
  });

  it('says why a backend cannot start', () => {
    const blocked = { ...base, backendStatus: 'unavailable' };
    expect(loadingPhase({ ...blocked, backendReason: 'storage' })).toBe('storage');
    expect(loadingPhase({ ...blocked, backendReason: 'pool' })).toBe('pool');
    expect(loadingPhase({ ...blocked, backendReason: null })).toBe('loading');
    expect(loadingPhase({ ...blocked, backendReason: 'storage', connected: true })).toBe('ready');
  });

  it('waits for window.ready when the manifest asks for it, but never longer than its time', () => {
    const waiting = { ...base, untilReady: true, connected: true };
    expect(loadingPhase(waiting)).toBe('loading');
    expect(loadingPhase({ ...waiting, appReady: true })).toBe('ready');
    expect(loadingPhase({ ...waiting, timedOut: true })).toBe('ready');
    expect(loadingPhase({ ...base, appReady: true })).toBe('loading');
  });
});
