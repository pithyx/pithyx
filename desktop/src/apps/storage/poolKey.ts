import type { NewPoolKey, UsbStick } from '../sdk/internal';
import type { Translate } from './shared';

/** The 14 groups of four after the prefix. */
export function keyGroups(key: string): string[] {
  return key.split('-').slice(1);
}

/** Two different indexes below `count`, in order; `random` returns numbers in [0, 1). */
export function pickTwo(count: number, random: () => number = Math.random): [number, number] {
  const first = Math.floor(random() * count);
  const second = (first + 1 + Math.floor(random() * (count - 1))) % count;
  return first < second ? [first, second] : [second, first];
}

/** A typed group as the key prints it: upper case, O read as 0, I and L as 1, spaces dropped. */
export function normalizeGroup(group: string): string {
  return group.toUpperCase().replace(/\s/g, '').replace(/O/g, '0').replace(/[IL]/g, '1');
}

/** The lines of the downloaded or printed key, in the user's language. */
export function keySheet(t: Translate, pool: string, poolKey: NewPoolKey, date: string): string[] {
  const name = pool || '<pool>';
  return [
    t('key.sheet.title'),
    t('key.sheet.pool', { pool: pool || '-' }),
    t('key.sheet.date', { date }),
    '',
    poolKey.key,
    '',
    t('key.sheet.hint'),
    '',
    t('key.sheet.emergency'),
    '',
    poolKey.hex,
    '',
    `  zpool import -f -N ${name}`,
    `  zfs load-key ${name}/pithyx`,
    `  zfs mount -a`,
    '',
    t('key.sheet.emergencyHint'),
  ];
}

/** How a stick is named, as the daemon names it: its label, else vendor and model, else its device. */
export function stickName(stick: Pick<UsbStick, 'device' | 'label' | 'vendor' | 'model'>): string {
  const label = stick.label?.trim() ?? '';
  if (label !== '') {
    return label;
  }
  const name = [stick.vendor, stick.model]
    .map((part) => part?.trim() ?? '')
    .filter((part) => part !== '')
    .join(' ');
  return name === '' ? stick.device : name;
}
