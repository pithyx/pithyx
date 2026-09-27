import { describe, expect, it } from 'vitest';
import { planRetry, type DesktopItem } from './sync';

function note(id: number, text: string, version = 1): DesktopItem {
  return { id, kind: 'note', app: null, name: null, text, column: 2, row: 3, version };
}

function shortcut(id: number, version = 1): DesktopItem {
  return { id, kind: 'shortcut', app: 'disks', name: null, text: null, column: 0, row: 0, version };
}

const same = (id: number) => id;

describe('planRetry', () => {
  it('saves moves and names again on top of the other device', () => {
    const before = new Map([[1, shortcut(1)]]);
    const plan = planRetry(
      [{ id: 1, column: 4, row: 1, name: 'Mine' }],
      before,
      [shortcut(1, 3)],
      same,
    );
    expect(plan).toEqual({ again: [{ id: 1, column: 4, row: 1, name: 'Mine' }], lost: [] });
  });

  it('saves note text again when only the place changed elsewhere', () => {
    const before = new Map([[1, note(1, 'old')]]);
    const plan = planRetry(
      [{ id: 1, text: 'new' }],
      before,
      [{ ...note(1, 'old', 2), column: 7 }],
      same,
    );
    expect(plan).toEqual({ again: [{ id: 1, text: 'new' }], lost: [] });
  });

  it('keeps note text on a new note when the text changed elsewhere, and still moves the note', () => {
    const before = new Map([[1, note(1, 'old')]]);
    const plan = planRetry([{ id: 1, text: 'mine', row: 5 }], before, [note(1, 'theirs', 2)], same);
    expect(plan).toEqual({
      again: [{ id: 1, row: 5 }],
      lost: [{ text: 'mine', column: 2, row: 3 }],
    });
  });

  it('keeps note text of a deleted note and drops moves of deleted items', () => {
    const before = new Map([
      [1, note(1, 'old')],
      [2, shortcut(2)],
    ]);
    const plan = planRetry(
      [
        { id: 1, text: 'mine' },
        { id: 2, column: 3 },
      ],
      before,
      [],
      same,
    );
    expect(plan).toEqual({ again: [], lost: [{ text: 'mine', column: 2, row: 3 }] });
  });

  it('drops changes of items whose creation failed', () => {
    const plan = planRetry([{ id: -1, column: 1 }], new Map(), [shortcut(1)], () => undefined);
    expect(plan).toEqual({ again: [], lost: [] });
  });
});
