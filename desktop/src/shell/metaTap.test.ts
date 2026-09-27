import { describe, expect, it } from 'vitest';
import { metaTap, type TapKey } from './metaTap';

function taps(events: TapKey[]): number {
  let alone = false;
  let count = 0;
  for (const event of events) {
    const next = metaTap(alone, event);
    alone = next.alone;
    count += next.tap ? 1 : 0;
  }
  return count;
}

describe('the Meta key tap', () => {
  it('counts Meta pressed and released alone, also with auto-repeat', () => {
    expect(
      taps([
        { type: 'keydown', key: 'Meta' },
        { type: 'keyup', key: 'Meta' },
      ]),
    ).toBe(1);
    expect(
      taps([
        { type: 'keydown', key: 'Meta' },
        { type: 'keydown', key: 'Meta', repeat: true },
        { type: 'keyup', key: 'Meta' },
      ]),
    ).toBe(1);
    expect(
      taps([
        { type: 'keydown', key: 'OS' },
        { type: 'keyup', key: 'OS' },
      ]),
    ).toBe(1);
  });

  it('ignores shortcuts with Meta and a lone keyup', () => {
    expect(
      taps([
        { type: 'keydown', key: 'Meta' },
        { type: 'keydown', key: 's' },
        { type: 'keyup', key: 's' },
        { type: 'keyup', key: 'Meta' },
      ]),
    ).toBe(0);
    expect(taps([{ type: 'keyup', key: 'Meta' }])).toBe(0);
    expect(
      taps([
        { type: 'keydown', key: 'a' },
        { type: 'keyup', key: 'a' },
      ]),
    ).toBe(0);
  });
});
