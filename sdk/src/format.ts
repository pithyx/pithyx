/**
 * Formatters in the user's language and time zone, built on `Intl`. They read the current locale on every call, so
 * they follow `localeChange` without the app doing anything. Dates arrive as ISO 8601 UTC and are shown in the
 * user's time zone.
 */

/** Formatters in the user's language and time zone, as `app.format` has them. None of them throws or needs a permission. */
export interface Formatters {
  /**
   * A date, for example "Sep 27, 2026".
   *
   * @param value - An ISO 8601 string or a `Date`.
   * @param options - `Intl.DateTimeFormat` options; `{ dateStyle: 'medium' }` when left out.
   * @returns The date in the user's time zone.
   */
  date(value: string | Date, options?: Intl.DateTimeFormatOptions): string;
  /**
   * A time of day, for example "14:05".
   *
   * @param value - An ISO 8601 string or a `Date`.
   * @param options - `Intl.DateTimeFormat` options; `{ timeStyle: 'short' }` when left out.
   * @returns The time in the user's time zone.
   */
  time(value: string | Date, options?: Intl.DateTimeFormatOptions): string;
  /**
   * Date and time.
   *
   * @param value - An ISO 8601 string or a `Date`.
   * @param options - `Intl.DateTimeFormat` options; medium date and short time when left out.
   * @returns Date and time in the user's time zone.
   */
  dateTime(value: string | Date, options?: Intl.DateTimeFormatOptions): string;
  /**
   * How long ago or how far ahead, for example "5 minutes ago", in the largest unit that fits.
   *
   * @param value - An ISO 8601 string or a `Date`.
   * @param now - The moment to compare with; the current time when left out.
   * @returns The distance in words, such as "yesterday" or "in 3 hours".
   */
  relative(value: string | Date, now?: Date): string;
  /**
   * A number with the user's separators, for example "1,234.5".
   *
   * @param value - The number.
   * @param options - `Intl.NumberFormat` options.
   * @returns The formatted number.
   */
  number(value: number, options?: Intl.NumberFormatOptions): string;
  /**
   * A byte count with decimal units, as the desktop shows sizes (10 GB, 4 TB).
   *
   * @param value - The size in bytes.
   * @returns The size with one decimal below 100 of its unit.
   */
  bytes(value: number): string;
  /**
   * A list, for example "Anna, Ben and Chris".
   *
   * @param values - The items, already as text.
   * @param options - `Intl.ListFormat` options; a conjunction when left out.
   * @returns The joined list.
   */
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

// Building an Intl formatter costs far more than using one, and a list formats the same way row after row.
const MAX_CACHED = 64;
const formatters = new Map<string, unknown>();

function cached<T>(kind: string, locale: string, options: object | undefined, make: () => T): T {
  const key = `${kind}|${locale}|${JSON.stringify(options ?? {})}`;
  let formatter = formatters.get(key) as T | undefined;
  if (formatter === undefined) {
    if (formatters.size >= MAX_CACHED) {
      formatters.clear();
    }
    formatter = make();
    formatters.set(key, formatter);
  }
  return formatter;
}

function toDate(value: string | Date): Date {
  return typeof value === 'string' ? new Date(value) : value;
}

/**
 * Formatters that follow the locale and time zone `current` returns at each call. `app.format` is built with it; an
 * app needs it only to format outside the desktop, for example in tests.
 *
 * @param current - Returns the locale and the time zone to use now.
 * @returns The formatters.
 */
export function createFormatters(
  current: () => {
    /** A BCP 47 tag such as `de-AT`. */
    locale: string;
    /** An IANA time zone such as `Europe/Berlin`. */
    timeZone: string;
  },
): Formatters {
  const dateTime = (value: string | Date, options: Intl.DateTimeFormatOptions) => {
    const { locale, timeZone } = current();
    const resolved = { ...options, timeZone: options.timeZone ?? timeZone };
    return cached('date', locale, resolved, () => new Intl.DateTimeFormat(locale, resolved)).format(
      toDate(value),
    );
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
      const { locale } = current();
      return cached(
        'relative',
        locale,
        undefined,
        () => new Intl.RelativeTimeFormat(locale, { numeric: 'auto' }),
      ).format(Math.round(amount), unit);
    },
    number: (value, options) => {
      const { locale } = current();
      return cached('number', locale, options, () => new Intl.NumberFormat(locale, options)).format(
        value,
      );
    },
    bytes: (value) => {
      let amount = value;
      let unit = 0;
      while (amount >= 1000 && unit < BYTE_UNITS.length - 1) {
        amount /= 1000;
        unit++;
      }
      const { locale } = current();
      const options: Intl.NumberFormatOptions = {
        style: 'unit',
        unit: BYTE_UNITS[unit],
        unitDisplay: 'short',
        maximumFractionDigits: amount < 100 ? 1 : 0,
      };
      return cached('number', locale, options, () => new Intl.NumberFormat(locale, options)).format(
        amount,
      );
    },
    list: (values, options = { type: 'conjunction' }) => {
      const { locale } = current();
      return cached('list', locale, options, () => new Intl.ListFormat(locale, options)).format(
        values,
      );
    },
  };
}
