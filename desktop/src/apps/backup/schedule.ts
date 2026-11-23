/** A job's schedule as the dialog offers it; anything else stays a cron expression. */
export type Schedule =
  | { kind: 'hourly'; minute: number }
  | { kind: 'daily'; time: string }
  | { kind: 'weekly'; day: number; time: string }
  | { kind: 'monthly'; day: number; time: string }
  | { kind: 'custom'; cron: string };

export type ScheduleKind = Schedule['kind'];

const NUMBER = /^\d{1,2}$/;
const TIME = /^(\d{1,2}):(\d{2})$/;

function inRange(text: string, min: number, max: number): number | undefined {
  if (!NUMBER.test(text)) {
    return undefined;
  }
  const value = Number(text);
  return value >= min && value <= max ? value : undefined;
}

const pad = (value: number) => String(value).padStart(2, '0');

/** "HH:MM" of a cron expression's minute and hour, or undefined when they are not plain numbers. */
function timeOf(minute: string, hour: string): string | undefined {
  const m = inRange(minute, 0, 59);
  const h = inRange(hour, 0, 23);
  return m === undefined || h === undefined ? undefined : `${pad(h)}:${pad(m)}`;
}

/** The hour and minute of "HH:MM", or undefined for anything else. */
export function parseTime(time: string): { hour: number; minute: number } | undefined {
  const match = TIME.exec(time.trim());
  if (match === null) {
    return undefined;
  }
  const hour = inRange(match[1] ?? '', 0, 23);
  const minute = inRange(match[2] ?? '', 0, 59);
  return hour === undefined || minute === undefined ? undefined : { hour, minute };
}

/** Reads a cron expression into the preset it matches; anything the presets cannot say is `custom`. */
export function scheduleOf(cron: string): Schedule {
  const fields = cron.trim().split(/\s+/);
  const custom: Schedule = { kind: 'custom', cron: cron.trim() };
  if (fields.length !== 5) {
    return custom;
  }
  const [minute = '', hour = '', dayOfMonth = '', month = '', dayOfWeek = ''] = fields;
  if (month !== '*') {
    return custom;
  }
  if (hour === '*' && dayOfMonth === '*' && dayOfWeek === '*') {
    const m = inRange(minute, 0, 59);
    return m === undefined ? custom : { kind: 'hourly', minute: m };
  }
  const time = timeOf(minute, hour);
  if (time === undefined) {
    return custom;
  }
  if (dayOfMonth === '*' && dayOfWeek === '*') {
    return { kind: 'daily', time };
  }
  if (dayOfMonth === '*') {
    const day = inRange(dayOfWeek, 0, 6);
    return day === undefined ? custom : { kind: 'weekly', day, time };
  }
  if (dayOfWeek === '*') {
    const day = inRange(dayOfMonth, 1, 28);
    return day === undefined ? custom : { kind: 'monthly', day, time };
  }
  return custom;
}

/** The cron expression of a schedule, or undefined while its time is not valid. */
export function cronOf(schedule: Schedule): string | undefined {
  if (schedule.kind === 'custom') {
    const cron = schedule.cron.trim().split(/\s+/).join(' ');
    return cron === '' ? undefined : cron;
  }
  if (schedule.kind === 'hourly') {
    return `${String(schedule.minute)} * * * *`;
  }
  const time = parseTime(schedule.time);
  if (time === undefined) {
    return undefined;
  }
  const at = `${String(time.minute)} ${String(time.hour)}`;
  switch (schedule.kind) {
    case 'daily':
      return `${at} * * *`;
    case 'weekly':
      return `${at} * * ${String(schedule.day)}`;
    case 'monthly':
      return `${at} ${String(schedule.day)} * *`;
  }
}

/** A schedule of another kind that keeps the time and day already chosen where it can. */
export function switchKind(schedule: Schedule, kind: ScheduleKind): Schedule {
  if (schedule.kind === kind) {
    return schedule;
  }
  const time = 'time' in schedule ? schedule.time : '02:00';
  switch (kind) {
    case 'hourly':
      return { kind, minute: 0 };
    case 'daily':
      return { kind, time };
    case 'weekly':
      return { kind, day: 0, time };
    case 'monthly':
      return { kind, day: 1, time };
    case 'custom':
      return { kind, cron: cronOf(schedule) ?? '' };
  }
}
