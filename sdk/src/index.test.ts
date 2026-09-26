import { describe, expect, it } from 'vitest';
import { vyxis } from './index';

describe('vyxis', () => {
  it('exports the SDK object', () => {
    expect(vyxis).toBeTypeOf('object');
  });
});
