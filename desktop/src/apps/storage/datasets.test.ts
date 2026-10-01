import { describe, expect, it } from 'vitest';
import {
  canRenameOrDelete,
  COMPRESSIONS,
  compressionOnly,
  datasetRole,
  isHiddenDataset,
  parseSize,
  RECORD_SIZES,
  sizeText,
} from './datasets';

describe('dataset roles', () => {
  it('follow the daemon layout rules', () => {
    expect(
      [
        'tank',
        'tank/pithyx',
        'tank/pithyx/shares',
        'tank/pithyx/later',
        'tank/pithyx/homes/anna',
        'tank/pithyx/apps/immich/db',
        'tank/pithyx/shares/media',
        'tank/pithyx/volumes/vm1',
        'tank/data',
        'tank/pithyxdata',
      ].map((name) => datasetRole('tank', name)),
    ).toEqual([
      'root',
      'structure',
      'structure',
      'structure',
      'managed',
      'managed',
      'share',
      'volume',
      'other',
      'other',
    ]);
  });

  it('allow renaming and deleting only shares, volumes and other datasets', () => {
    expect(canRenameOrDelete('share')).toBe(true);
    expect(canRenameOrDelete('volume')).toBe(true);
    expect(canRenameOrDelete('other')).toBe(true);
    expect(canRenameOrDelete('root')).toBe(false);
    expect(canRenameOrDelete('structure')).toBe(false);
    expect(canRenameOrDelete('managed')).toBe(false);
    expect(compressionOnly('root', 'filesystem')).toBe(true);
    expect(compressionOnly('share', 'volume')).toBe(true);
    expect(compressionOnly('managed', 'filesystem')).toBe(false);
  });

  it('offer the compressions and record sizes the daemon accepts', () => {
    expect(COMPRESSIONS).toHaveLength(33);
    expect(COMPRESSIONS).not.toContain('on');
    expect(RECORD_SIZES[0]).toBe(4096);
    expect(RECORD_SIZES.at(-1)).toBe(1024 * 1024);
  });
});

describe('size fields', () => {
  it('parse decimal numbers with a point or a comma', () => {
    expect(parseSize('20', 'gb')).toBe(20e9);
    expect(parseSize('1,5', 'tb')).toBe(1.5e12);
    expect(parseSize(' 0.25 ', 'mb')).toBe(250000);
    expect(parseSize('', 'gb')).toBeNull();
    expect(parseSize('-1', 'gb')).toBeUndefined();
    expect(parseSize('1e3', 'gb')).toBeUndefined();
    expect(parseSize('ten', 'gb')).toBeUndefined();
  });

  it('show a size in the largest unit it fills and read it back', () => {
    expect(sizeText(null, 'en')).toEqual({ text: '', unit: 'gb' });
    expect(sizeText(20e9, 'en')).toEqual({ text: '20', unit: 'gb' });
    expect(sizeText(1.5e12, 'de')).toEqual({ text: '1,5', unit: 'tb' });
    expect(sizeText(67108864, 'en')).toEqual({ text: '67.109', unit: 'mb' });
    const { text, unit } = sizeText(21474836480, 'de');
    expect(parseSize(text, unit)).toBe(21475000000);
  });
});

describe('hidden datasets', () => {
  it('hide the Pithyx layout and what the daemon manages, and show the root, shares and volumes', () => {
    for (const name of [
      'tank/pithyx',
      'tank/pithyx/apps',
      'tank/pithyx/apps/immich',
      'tank/pithyx/system',
      'tank/pithyx/homes',
      'tank/pithyx/homes/alex',
      'tank/pithyx/shares',
      'tank/pithyx/cache',
      'tank/pithyx/volumes',
    ]) {
      expect(isHiddenDataset('tank', name)).toBe(true);
    }
    for (const name of [
      'tank',
      'tank/pithyx/shares/photos',
      'tank/pithyx/volumes/vm1',
      'tank/old',
    ]) {
      expect(isHiddenDataset('tank', name)).toBe(false);
    }
  });
});
