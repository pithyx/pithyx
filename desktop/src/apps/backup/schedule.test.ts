import { describe, expect, it } from 'vitest';
import { cronOf, parseTime, scheduleOf, switchKind } from './schedule';

describe('backup schedules', () => {
  it('read the presets out of cron and back', () => {
    for (const [cron, schedule] of [
      ['15 * * * *', { kind: 'hourly', minute: 15 }],
      ['0 2 * * *', { kind: 'daily', time: '02:00' }],
      ['30 23 * * 0', { kind: 'weekly', day: 0, time: '23:30' }],
      ['5 4 1 * *', { kind: 'monthly', day: 1, time: '04:05' }],
    ] as const) {
      expect(scheduleOf(cron)).toEqual(schedule);
      expect(cronOf(schedule)).toBe(cron);
    }
  });

  it('keep anything the presets cannot say as cron', () => {
    for (const cron of ['*/15 * * * *', '0 2 * * 1-5', '0 2 31 * *', '0 2 * 1 *', '0 2 * *']) {
      expect(scheduleOf(cron)).toEqual({ kind: 'custom', cron });
    }
    expect(cronOf({ kind: 'custom', cron: '  0  2 * * 1-5 ' })).toBe('0 2 * * 1-5');
    expect(cronOf({ kind: 'custom', cron: ' ' })).toBeUndefined();
  });

  it('need a valid time', () => {
    expect(parseTime('7:05')).toEqual({ hour: 7, minute: 5 });
    expect(parseTime('24:00')).toBeUndefined();
    expect(parseTime('12:60')).toBeUndefined();
    expect(cronOf({ kind: 'daily', time: 'noon' })).toBeUndefined();
  });

  it('keep the chosen time when the kind changes', () => {
    expect(switchKind({ kind: 'daily', time: '03:30' }, 'weekly')).toEqual({
      kind: 'weekly',
      day: 0,
      time: '03:30',
    });
    expect(switchKind({ kind: 'weekly', day: 3, time: '03:30' }, 'custom')).toEqual({
      kind: 'custom',
      cron: '30 3 * * 3',
    });
  });
});
