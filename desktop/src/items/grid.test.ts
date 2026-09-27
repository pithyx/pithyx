import { describe, expect, it } from 'vitest';
import {
  CELL_HEIGHT,
  CELL_WIDTH,
  GRID_PADDING,
  cellAt,
  firstFreeCell,
  gridSize,
  iconsInBox,
  moveIcons,
  nearestCell,
  neighbor,
  placeIcons,
  placeNote,
  type Cell,
} from './grid';

const size = { columns: 4, rows: 3 };

function cells(placed: ReadonlyMap<number, Cell>): Record<number, string> {
  return Object.fromEntries(
    [...placed].map(([id, cell]) => [id, `${String(cell.column)},${String(cell.row)}`]),
  );
}

describe('gridSize', () => {
  it('fits whole cells inside the padding and never returns an empty grid', () => {
    expect(
      gridSize({
        width: 2 * GRID_PADDING + 4 * CELL_WIDTH + 5,
        height: 2 * GRID_PADDING + 3 * CELL_HEIGHT,
      }),
    ).toEqual({
      columns: 4,
      rows: 3,
    });
    expect(gridSize({ width: 10, height: 10 })).toEqual({ columns: 1, rows: 1 });
  });
});

describe('cells from points', () => {
  it('rounds to the nearest cell for drops and floors for hits', () => {
    const point = { x: GRID_PADDING + CELL_WIDTH * 1.6, y: GRID_PADDING + CELL_HEIGHT * 0.4 };
    expect(nearestCell(point)).toEqual({ column: 2, row: 0 });
    expect(cellAt(point)).toEqual({ column: 1, row: 0 });
    expect(nearestCell({ x: -500, y: -500 })).toEqual({ column: 0, row: 0 });
  });
});

describe('placeIcons', () => {
  it('keeps every icon in its own cell when it fits', () => {
    const placed = placeIcons(
      [
        { id: 1, column: 0, row: 0 },
        { id: 2, column: 3, row: 2 },
      ],
      size,
    );
    expect(cells(placed)).toEqual({ 1: '0,0', 2: '3,2' });
  });

  it('moves icons from outside a small screen into the nearest free cell without losing any', () => {
    const placed = placeIcons(
      [
        { id: 1, column: 3, row: 2 },
        { id: 2, column: 9, row: 1 },
        { id: 3, column: 9, row: 9 },
      ],
      size,
    );
    expect(cells(placed)).toEqual({ 1: '3,2', 2: '3,1', 3: '4,0' });
  });

  it('gives the cell to the older item when two devices put icons on the same cell', () => {
    const placed = placeIcons(
      [
        { id: 7, column: 1, row: 1 },
        { id: 4, column: 1, row: 1 },
      ],
      size,
    );
    expect(cells(placed)).toEqual({ 4: '1,1', 7: '1,2' });
  });
});

describe('firstFreeCell', () => {
  it('fills the first column from the top, then the next', () => {
    const placed = placeIcons(
      [
        { id: 1, column: 0, row: 0 },
        { id: 2, column: 0, row: 1 },
        { id: 3, column: 0, row: 2 },
      ],
      size,
    );
    expect(firstFreeCell(placed, size)).toEqual({ column: 1, row: 0 });
  });
});

describe('moveIcons', () => {
  const placed = new Map<number, Cell>([
    [1, { column: 0, row: 0 }],
    [2, { column: 0, row: 1 }],
    [3, { column: 2, row: 1 }],
  ]);

  it('moves a group by the same distance', () => {
    expect(cells(moveIcons(placed, [1, 2], { column: 1, row: 1 }, size))).toEqual({
      1: '1,1',
      2: '1,2',
    });
  });

  it('sends an icon that lands on another one to the nearest free cell', () => {
    expect(cells(moveIcons(placed, [1], { column: 2, row: 1 }, size))).toEqual({ 1: '2,2' });
  });

  it('lets a moved icon take a cell another moved icon leaves', () => {
    expect(cells(moveIcons(placed, [1, 2], { column: 0, row: 1 }, size))).toEqual({
      1: '0,1',
      2: '0,2',
    });
  });

  it('keeps icons on the screen at its edges', () => {
    expect(cells(moveIcons(placed, [3], { column: 5, row: -4 }, size))).toEqual({ 3: '3,0' });
  });
});

describe('placeNote', () => {
  it('keeps the whole note on the screen', () => {
    expect(placeNote({ column: 3, row: 2 }, size)).toEqual({ column: 1, row: 1 });
    expect(placeNote({ column: 3, row: 2 }, { columns: 2, rows: 1 })).toEqual({
      column: 0,
      row: 0,
    });
  });
});

describe('iconsInBox', () => {
  it('selects icons whose cell overlaps the frame', () => {
    const placed = new Map<number, Cell>([
      [1, { column: 0, row: 0 }],
      [2, { column: 1, row: 0 }],
      [3, { column: 3, row: 2 }],
    ]);
    const box = { x: GRID_PADDING + CELL_WIDTH - 1, y: 0, width: 10, height: 20 };
    expect(iconsInBox(placed, box)).toEqual([1, 2]);
    expect(iconsInBox(placed, { x: 0, y: 900, width: 10, height: 10 })).toEqual([]);
  });
});

describe('neighbor', () => {
  const placed = new Map<number, Cell>([
    [1, { column: 0, row: 0 }],
    [2, { column: 0, row: 2 }],
    [3, { column: 1, row: 1 }],
    [4, { column: 3, row: 0 }],
  ]);

  it('prefers the same column going down and up', () => {
    expect(neighbor(placed, 1, 'down')).toBe(2);
    expect(neighbor(placed, 2, 'up')).toBe(1);
  });

  it('prefers the same row going to the side, and takes the nearest icon off the row without one', () => {
    expect(neighbor(placed, 1, 'right')).toBe(4);
    expect(neighbor(placed, 2, 'right')).toBe(3);
  });

  it('returns null at the edge', () => {
    expect(neighbor(placed, 1, 'left')).toBeNull();
    expect(neighbor(placed, 4, 'up')).toBeNull();
  });
});
