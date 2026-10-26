import { describe, expect, it } from 'vitest';
import { keyGroups, keySheet, normalizeGroup, pickTwo } from './poolKey';
import type { Translate } from './shared';

const key = 'PXPK-0123-4567-89AB-CDEF-GHJK-MNPQ-RSTV-WXYZ-0000-1111-2222-3333-4444-5555';
const hex = 'ab'.repeat(32);

describe('pool key confirmation', () => {
  it('splits the key into its 14 groups after the prefix', () => {
    expect(keyGroups(key)).toHaveLength(14);
    expect(keyGroups(key)[0]).toBe('0123');
    expect(keyGroups(key)[13]).toBe('5555');
  });

  it('always asks for two different groups, in order', () => {
    for (const first of [0, 0.3, 0.5, 0.999]) {
      for (const second of [0, 0.4, 0.999]) {
        const values = [first, second];
        const [a, b] = pickTwo(14, () => values.shift() ?? 0);
        expect(a).toBeLessThan(b);
        expect(a).toBeGreaterThanOrEqual(0);
        expect(b).toBeLessThan(14);
      }
    }
  });

  it('forgives the slips the key itself forgives', () => {
    expect(normalizeGroup(' gh jk ')).toBe('GHJK');
    expect(normalizeGroup('o1Il')).toBe('0111');
  });
});

describe('the key sheet', () => {
  const t = ((text: string, params?: Record<string, string>) =>
    params === undefined ? text : `${text} ${JSON.stringify(params)}`) as unknown as Translate;

  it('carries the key, its hex form and the commands that open the pool without Pithyx', () => {
    const sheet = keySheet(t, 'tank', { key, hex }, '26 Oct 2026');

    expect(sheet).toContain(key);
    expect(sheet).toContain(hex);
    expect(sheet).toContain('  zpool import -f -N tank');
    expect(sheet).toContain('  zfs load-key tank/pithyx');
    expect(sheet).toContain('  zfs mount -a');
    expect(sheet.indexOf(key)).toBeLessThan(sheet.indexOf(hex));
  });

  it('names a placeholder in the commands while the pool has no name yet', () => {
    expect(keySheet(t, '', { key, hex }, 'today')).toContain('  zfs load-key <pool>/pithyx');
  });
});
