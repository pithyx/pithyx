import { describe, expect, it } from 'vitest';
import { randomId } from './browser';

describe('random ids without a secure context', () => {
  it('are version 4 UUIDs from getRandomValues', () => {
    const ids = Array.from({ length: 1000 }, randomId);
    for (const id of ids) {
      expect(id).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);
    }
    expect(new Set(ids).size).toBe(ids.length);
  });
});
