/**
 * Formatters in the user's language and time zone, built on `Intl`. They read the current locale on every call, so
 * they follow `localeChange` without the app doing anything. Dates arrive as ISO 8601 UTC and are shown in the
 * user's time zone.
 */

export interface Formatters {
  /** A date, for example "Sep 27, 2026". */
  date(value: string | Date, options?: Intl.DateTimeFormatOptions): string;
  /** A time of day, for example "14:05". */
  time(value: string | Date, options?: Intl.DateTimeFormatOptions): string;
  /** Date and time. */
  dateTime(value: string | Date, options?: Intl.DateTimeFormatOptions): string;
  /** How long ago or how far ahead, for example "5 minutes ago". */
  relative(value: string | Date, now?: Date): string;
  number(value: number, options?: Intl.NumberFormatOptions): string;
  /** A byte count with decimal units, as the desktop shows sizes (10 GB, 4 TB). */
  bytes(value: number): string;
  /** A list, for example "Anna, Ben and Chris". */
  list(values: readonly string[], options?: Intl.ListFormatOptions): string;
}

const BYTE_UNITS = ['byte', 'kilobyte', 'megabyte', 'gigabyte', 'terabyte', 'petabyte'] as const;

const RELATIVE_STEPS: [Intl.RelativeTimeFormatUnit, number][] = [
  ['second', 60],
  ['minute', 60],
  ['hour', 24],
  ['day', 7],
  ['week', 4.35],
  ['month', 12],
  ['year', Infinity],
];

function toDate(value: string | Date): Date {
  return typeof value === 'string' ? new Date(value) : value;
}

export function createFormatters(current: () => { locale: string; timeZone: string }): Formatters {
  const dateTime = (value: string | Date, options: Intl.DateTimeFormatOptions) => {
    const { locale, timeZone } = current();
    return new Intl.DateTimeFormat(locale, {
      ...options,
      timeZone: options.timeZone ?? timeZone,
    }).format(toDate(value));
  };
  return {
    date: (value, options = { dateStyle: 'medium' }) => dateTime(value, options),
    time: (value, options = { timeStyle: 'short' }) => dateTime(value, options),
    dateTime: (value, options = { dateStyle: 'medium', timeStyle: 'short' }) =>
      dateTime(value, options),
    relative: (value, now = new Date()) => {
      let amount = (toDate(value).getTime() - now.getTime()) / 1000;
      let unit: Intl.RelativeTimeFormatUnit = 'second';
      for (const [step, size] of RELATIVE_STEPS) {
        unit = step;
        if (Math.abs(amount) < size) {
          break;
        }
        amount /= size;
      }
      return new Intl.RelativeTimeFormat(current().locale, { numeric: 'auto' }).format(
        Math.round(amount),
        unit,
      );
    },
    number: (value, options) => new Intl.NumberFormat(current().locale, options).format(value),
    bytes: (value) => {
      let amount = value;
      let unit = 0;
      while (amount >= 1000 && unit < BYTE_UNITS.length - 1) {
        amount /= 1000;
        unit++;
      }
      return new Intl.NumberFormat(current().locale, {
        style: 'unit',
        unit: BYTE_UNITS[unit],
        unitDisplay: 'short',
        maximumFractionDigits: amount < 100 ? 1 : 0,
      }).format(amount);
    },
    list: (values, options = { type: 'conjunction' }) =>
      new Intl.ListFormat(current().locale, options).format(values),
  };
}
