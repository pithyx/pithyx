const FIELDS: readonly (readonly [number, number])[] = [
  [0, 59],
  [0, 23],
  [1, 31],
  [1, 12],
  [0, 7],
];

function number(text: string): number | undefined {
  return /^(0|[1-9][0-9]?)$/.test(text) ? Number(text) : undefined;
}

function isValidItem(item: string, [min, max]: readonly [number, number]): boolean {
  const slash = item.indexOf('/');
  const range = slash < 0 ? item : item.slice(0, slash);
  if (slash >= 0) {
    const step = number(item.slice(slash + 1));
    if (step === undefined || step < 1 || step > max - min + 1) {
      return false;
    }
  }
  if (range === '*') {
    return true;
  }
  const dash = range.indexOf('-');
  if (dash < 0) {
    // A step needs a range to walk: "5/10" is refused.
    const value = number(range);
    return slash < 0 && value !== undefined && value >= min && value <= max;
  }
  const from = number(range.slice(0, dash));
  const to = number(range.slice(dash + 1));
  return from !== undefined && to !== undefined && from >= min && to <= max && from <= to;
}

/**
 * The cron syntax of manifest schedules: five fields separated by single spaces (minute, hour, day of month, month,
 * day of week with 0 and 7 for Sunday). A field is a comma list of `*`, numbers and ranges `a-b`; `*` and ranges may
 * take a step `/n`. No names, no leading zeros.
 */
export function isValidCron(cron: string): boolean {
  const fields = cron.split(' ');
  return (
    fields.length === FIELDS.length &&
    fields.every(
      (field, index) =>
        field !== '' &&
        field.split(',').every((item) => isValidItem(item, FIELDS[index] ?? [0, 0])),
    )
  );
}
