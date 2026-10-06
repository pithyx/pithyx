import { describe, expect, it } from 'vitest';
import { DEFAULT_VIEW, describeSource, filterOf, groupSources, toggleLevel } from './filters';
import { logQuery } from '../sdk/logs';

const now = Date.parse('2026-09-30T12:00:00Z');

describe('log filters', () => {
  it('asks for the last day without debug by default', () => {
    expect(filterOf(DEFAULT_VIEW, now)).toEqual({
      from: '2026-09-29T12:00:00.000Z',
      level: ['info', 'warn', 'error'],
    });
  });

  it('leaves out what does not filter: every level, no source, all time, blank search', () => {
    expect(
      filterOf(
        { levels: ['debug', 'info', 'warn', 'error'], sources: [], range: 'all', search: '  ' },
        now,
      ),
    ).toEqual({});
  });

  it('passes sources and the trimmed search', () => {
    expect(
      filterOf(
        { ...DEFAULT_VIEW, range: 'hour', sources: ['app/org.pithyx.files'], search: ' tank ' },
        now,
      ),
    ).toMatchObject({
      from: '2026-09-30T11:00:00.000Z',
      source: ['app/org.pithyx.files'],
      search: 'tank',
    });
  });

  it('repeats array keys in the query string', () => {
    expect(
      logQuery({ level: ['warn', 'error'], source: ['system/kernel'], search: '', format: 'text' }),
    ).toBe('level=warn&level=error&source=system%2Fkernel&format=text');
  });

  it('groups sources: the daemon, system services, apps', () => {
    expect(
      groupSources(['app/org.pithyx.files', 'system/smbd', 'daemon', 'system/kernel']),
    ).toEqual({
      daemon: ['daemon'],
      system: ['system/kernel', 'system/smbd'],
      app: ['app/org.pithyx.files'],
    });
    expect(describeSource('system/zfs-zed')).toEqual({ group: 'system', name: 'zfs-zed' });
  });

  it("names a core app's entries from before its org.pithyx id after the app", () => {
    expect(describeSource('app/files')).toEqual({ group: 'app', name: 'org.pithyx.files' });
    expect(describeSource('app/org.pithyx.files')).toEqual({
      group: 'app',
      name: 'org.pithyx.files',
    });
    expect(describeSource('app/com.mueller.files')).toEqual({
      group: 'app',
      name: 'com.mueller.files',
    });
  });

  it('keeps at least one level and the order of levels', () => {
    expect(toggleLevel(['error'], 'error')).toEqual(['error']);
    expect(toggleLevel(['error'], 'debug')).toEqual(['debug', 'error']);
    expect(toggleLevel(['info', 'warn'], 'info')).toEqual(['warn']);
  });
});
