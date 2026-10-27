import { describe, expect, it } from 'vitest';
import tiers from '../../../../sdk/schema/fixtures/tiers/cases.json';
import type { PoolList } from '../sdk/store';
import {
  accessOf,
  appPools,
  deviceNote,
  elevatedKind,
  folderPath,
  inCategory,
  manifestFacts,
  requirementHints,
  screenshotUrl,
  speaksLanguage,
  tierOf,
  withSubject,
} from './model';

describe('tierOf', () => {
  // The shared corpus the box and the CLI are held to (D388).
  it.each(Object.entries(tiers.cases))('gives %s the tier %s', (right, tier) => {
    expect(tierOf(right)).toBe(tier);
  });
});

describe('manifestFacts', () => {
  it('reads what the install dialog shows', () => {
    const facts = manifestFacts({
      name: { en: 'Chat', de: 'Chat' },
      author: { name: 'Jan Mueller', url: 'https://mueller.example' },
      permissions: {
        notifications: { reason: { en: 'Tells you about new messages.' } },
        'directory.read': {
          reason: { en: 'Lists who you can chat with.', de: 'Listet Kontakte.' },
        },
        'network.internet': {
          reason: { en: 'Fetches link previews.' },
          hosts: ['api.example.org'],
        },
        'device:/dev/dri/renderD128': { reason: { en: 'Scales images.' }, optional: true },
      },
      storage: [
        { id: 'attachments', name: { en: 'Attachments' }, access: 'readWrite', required: true },
      ],
      backend: { root: true },
    });
    expect(facts).toMatchObject({
      author: 'Jan Mueller',
      permissions: ['notifications', 'directory.read', 'network.internet'],
      hosts: ['api.example.org'],
      storage: [{ id: 'attachments', access: 'readWrite', required: true }],
      backend: true,
      root: true,
      elevated: ['device:/dev/dri/renderD128'],
    });
    expect(facts.reasons['directory.read']).toEqual({
      en: 'Lists who you can chat with.',
      de: 'Listet Kontakte.',
    });
    expect(facts.reasons['device:/dev/dri/renderD128']).toEqual({ en: 'Scales images.' });
  });

  it('has no reason for a right of an app installed before reasons were required', () => {
    const facts = manifestFacts({ name: { en: 'Old' }, permissions: { notifications: {} } });
    expect(facts.permissions).toEqual(['notifications']);
    expect(facts.reasons).toEqual({});
  });

  it('treats a frontend-only manifest as one without backend, root or rights', () => {
    const facts = manifestFacts({ name: { en: 'Notes' }, permissions: {} });
    expect(facts).toMatchObject({
      backend: false,
      root: false,
      elevated: [],
      storage: [],
      author: null,
    });
    expect(manifestFacts(null).permissions).toEqual([]);
  });
});

describe('accessOf', () => {
  it('says everybody for the built-in group, administrators for no grant, some otherwise', () => {
    expect(accessOf([{ subjectKind: 'group', subjectId: 100, level: 'allow' }], 100)).toBe(
      'everybody',
    );
    expect(accessOf([], 100)).toBe('admins');
    expect(accessOf([{ subjectKind: 'user', subjectId: 7, level: 'allow' }], 100)).toBe('some');
  });
});

describe('withSubject', () => {
  it('adds and removes one grant and keeps the others', () => {
    const rita = withSubject([], 'user', 7, true);
    expect(rita).toEqual([{ subjectKind: 'user', subjectId: 7, level: 'allow' }]);
    const both = withSubject(rita, 'group', 7, true);
    expect(both).toHaveLength(2);
    expect(withSubject(both, 'user', 7, false)).toEqual([
      { subjectKind: 'group', subjectId: 7, level: 'allow' },
    ]);
    expect(withSubject(rita, 'user', 7, true)).toEqual(rita);
  });
});

describe('appPools', () => {
  it('offers the online writable pools by name', () => {
    const pool = (name: string, state: string, readOnly = false) =>
      ({ name, state, readOnly }) as PoolList['pools'][number];
    const pools = {
      pools: [
        pool('tank', 'ONLINE'),
        pool('backup', 'DEGRADED'),
        pool('old', 'FAULTED'),
        pool('ro', 'ONLINE', true),
      ],
      datasets: [],
    } as unknown as PoolList;
    expect(appPools(pools)).toEqual(['backup', 'tank']);
  });
});

describe('folderPath', () => {
  it('normalizes what an administrator types into a relative path', () => {
    expect(folderPath('/Photos/2026/')).toBe('Photos/2026');
    expect(folderPath('Photos\\2026')).toBe('Photos/2026');
    expect(folderPath('  ')).toBe('');
    expect(folderPath('a//b')).toBe('a/b');
  });
});

describe('elevatedKind', () => {
  it('tells the rights apart for their risk texts', () => {
    expect(elevatedKind('host.network')).toEqual({ kind: 'hostNetwork' });
    expect(elevatedKind('host.pid')).toEqual({ kind: 'hostPid' });
    expect(elevatedKind('device:/dev/dri/renderD128')).toEqual({
      kind: 'device',
      value: '/dev/dri/renderD128',
    });
    expect(elevatedKind('capability:NET_ADMIN')).toEqual({
      kind: 'capability',
      value: 'NET_ADMIN',
    });
    expect(elevatedKind('docker.socket')).toEqual({ kind: 'dockerSocket' });
    expect(elevatedKind('docker.socket.raw')).toEqual({ kind: 'dockerSocketRaw' });
    expect(elevatedKind('device:/dev/dri')).toEqual({
      kind: 'device',
      value: '/dev/dri',
      deviceClass: 'dri',
    });
    expect(elevatedKind('host.full')).toEqual({ kind: 'unknown', value: 'host.full' });
  });
});

describe('deviceNote', () => {
  const devices = [
    { path: '/dev/serial', optional: false, present: false },
    { path: '/dev/dri', optional: true, present: false },
    { path: '/dev/net/tun', optional: false, present: true },
  ];

  it('stops the install for a missing required device and only hints at an optional one', () => {
    expect(deviceNote('device:/dev/serial', devices, 'install')).toBe('elevated.deviceMissing');
    expect(deviceNote('device:/dev/dri', devices, 'install')).toBe(
      'elevated.deviceMissingOptional',
    );
    expect(deviceNote('device:/dev/net/tun', devices, 'install')).toBeNull();
  });

  it('says on the app page what the background service does without it', () => {
    expect(deviceNote('device:/dev/serial', devices, 'installed')).toBe('elevated.deviceAbsent');
    expect(deviceNote('device:/dev/dri', devices, 'installed')).toBe(
      'elevated.deviceAbsentOptional',
    );
    expect(deviceNote('host.network', devices, 'installed')).toBeNull();
  });
});

describe('store fields (D389)', () => {
  it('reads the store fields of a manifest and leaves out what it lacks', () => {
    const facts = manifestFacts({
      source: 'private',
      arch: ['amd64'],
      privacy: { sends: 'telemetry', note: { en: 'Crash reports.' } },
      maturity: 'beta',
      categories: ['media'],
      screenshots: [{ file: 'frontend/shot.png' }, { caption: { en: 'No file' } }],
      requirements: { memory: 512 },
      audience: 'admins',
    });
    expect(facts.privacy).toEqual({ sends: 'telemetry', note: { en: 'Crash reports.' } });
    expect(facts.maturity).toBe('beta');
    expect(facts.screenshots).toEqual([{ file: 'frontend/shot.png', caption: null }]);
    expect(facts.requirements).toEqual({ memory: 512, disk: null });
    expect(facts.audience).toBe('admins');
    expect(manifestFacts({}).maturity).toBe('stable');
    expect(manifestFacts({ privacy: { sends: 'everything' } }).privacy).toBeNull();
  });

  it('matches the user language by its base and says nothing without locales', () => {
    expect(speaksLanguage(['en', 'pt-BR'], 'pt-PT')).toBe(true);
    expect(speaksLanguage(['en'], 'de')).toBe(false);
    expect(speaksLanguage(['DE'], 'de')).toBe(true);
    expect(speaksLanguage([], 'de')).toBeNull();
  });

  it('hints only where the box falls short, in MB', () => {
    const mb = 1024 * 1024;
    expect(requirementHints({ memory: 2048, disk: 100 }, 1024 * mb, 50 * mb)).toEqual([
      { kind: 'memory', needed: 2048, available: 1024 },
      { kind: 'disk', needed: 100, available: 50 },
    ]);
    expect(requirementHints({ memory: 2048, disk: 100 }, 4096 * mb, 500 * mb)).toEqual([]);
    expect(requirementHints({ memory: 2048, disk: 100 }, null, null)).toEqual([]);
  });

  it('filters catalog apps by category', () => {
    const apps = [
      { id: 'a', categories: ['media'] },
      { id: 'b', categories: [] },
    ];
    expect(inCategory(apps, 'media').map((app) => app.id)).toEqual(['a']);
    expect(inCategory(apps, null)).toHaveLength(2);
  });

  it('serves a screenshot from the frontend of the installed app', () => {
    expect(screenshotUrl('com.example.chat', 'frontend/screenshots/a.png')).toBe(
      '/apps/com.example.chat/screenshots/a.png',
    );
  });
});
