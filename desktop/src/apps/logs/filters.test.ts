import { describe, expect, it } from 'vitest';
import {
  addCounts,
  addSources,
  countOf,
  DEFAULT_VIEW,
  describeSource,
  filterOf,
  groupSources,
  matches,
} from './filters';
import { logQuery, type LogEntry } from '../sdk/logs';

const now = Date.parse('2026-09-30T12:00:00Z');

const entry = (fields: Partial<LogEntry>): LogEntry => ({
  id: 1,
  time: '2026-09-30T11:59:00Z',
  level: 'info',
  source: 'daemon',
  message: 'started',
  requestId: null,
  user: null,
  data: null,
  archived: false,
  ...fields,
});

describe('log filters', () => {
  it('asks for the last day without debug by default', () => {
    expect(filterOf(DEFAULT_VIEW, now)).toEqual({
      from: '2026-09-29T12:00:00.000Z',
      level: ['info', 'warn', 'error'],
    });
  });

  it('leaves out what does not filter: debug with every level, no source, all time, blank search', () => {
    expect(filterOf({ level: 'debug', source: null, range: 'all', search: '  ' }, now)).toEqual({});
  });

  it('passes the warnings, the source and the trimmed search', () => {
    expect(
      filterOf(
        {
          level: 'warnings',
          range: 'hour',
          source: 'app/org.pithyx.files',
          search: ' tank ',
        },
        now,
      ),
    ).toEqual({
      from: '2026-09-30T11:00:00.000Z',
      level: ['warn'],
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
    expect(describeSource('app/com.mueller.files')).toEqual({
      group: 'app',
      name: 'com.mueller.files',
    });
  });

  it('decides like the daemon whether a live entry belongs in the view', () => {
    const view = { ...DEFAULT_VIEW, source: 'app/org.pithyx.files', search: 'A.JPG' };
    expect(
      matches(view, entry({ source: 'app/files', level: 'warn', data: { file: 'a.jpg' } })),
    ).toBe(true);
    expect(matches(view, entry({ source: 'app/files', level: 'debug', message: 'a.jpg' }))).toBe(
      false,
    );
    expect(matches(view, entry({ source: 'system/smbd', message: 'a.jpg' }))).toBe(false);
    expect(matches({ ...DEFAULT_VIEW, level: 'errors' }, entry({ level: 'warn' }))).toBe(false);
  });

  it('adds the sources of live entries under their current name', () => {
    expect(
      addSources(['daemon'], [entry({ source: 'app/files' }), entry({ source: 'daemon' })]),
    ).toEqual(['app/org.pithyx.files', 'daemon']);
  });

  it('adds the warnings and errors of live entries to their source', () => {
    const counts = addCounts(
      [{ source: 'app/org.pithyx.files', warnings: 1, errors: 0 }],
      [
        entry({ source: 'app/files', level: 'error' }),
        entry({ source: 'system/smbd', level: 'warn' }),
        entry({ source: 'system/smbd', level: 'info' }),
      ],
    );
    expect(countOf(counts, 'app/org.pithyx.files')).toEqual({ warnings: 1, errors: 1 });
    expect(countOf(counts, 'system/smbd')).toEqual({ warnings: 1, errors: 0 });
    expect(countOf(counts, null)).toEqual({ warnings: 2, errors: 1 });
  });
});
