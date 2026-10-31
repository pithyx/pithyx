import { describe, expect, it } from 'vitest';
import { firstPlace, placesOf } from './FileDialog';

const share = (id: number, name: string, kind?: 'share' | 'home' | 'userHome') => ({
  id,
  name,
  access: 'readWrite' as const,
  path: null,
  unavailable: null,
  ...(kind === undefined ? {} : { kind }),
});

describe('placesOf', () => {
  it('puts the own home first and keeps other homes apart, sorted by name', () => {
    const places = placesOf([
      share(1, 'media', 'share'),
      share(2, 'zoe', 'userHome'),
      share(3, 'home', 'home'),
      share(4, 'Photos'),
      share(5, 'anna', 'userHome'),
    ]);
    expect(places.own.map((each) => each.id)).toEqual([3]);
    expect(places.shared.map((each) => each.id)).toEqual([1, 4]);
    expect(places.homes.map((each) => each.name)).toEqual(['anna', 'zoe']);
  });

  it('has no homes entry for a user who sees no other homes', () => {
    expect(placesOf([share(1, 'media'), share(2, 'home', 'home')]).homes).toEqual([]);
  });
});

describe('firstPlace', () => {
  it('opens in the own home, else in the first shared folder that is there', () => {
    expect(firstPlace([share(1, 'media'), share(3, 'home', 'home')])).toBe('share:3');
    expect(
      firstPlace([
        { ...share(1, 'media'), unavailable: 'notMounted' as const },
        share(2, 'Photos'),
      ]),
    ).toBe('share:2');
  });

  it('falls back to the homes of others, and to nothing without any place', () => {
    expect(firstPlace([share(2, 'zoe', 'userHome')])).toBe('homes');
    expect(firstPlace([])).toBeNull();
  });
});
