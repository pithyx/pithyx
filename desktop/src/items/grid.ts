// The desktop's icon grid. Items are stored by cell, so the same arrangement works on every screen of the user; a
// screen that is too small shows an item in the next free cell without changing where it is stored.

/** Keep in step with --desktop-cell-width, --desktop-cell-height and --desktop-grid-padding in @pithyx/ui's tokens.css. */
export const CELL_WIDTH = 96;
export const CELL_HEIGHT = 112;
export const GRID_PADDING = 12;
/** A sticky note covers this many cells (it is 230 by 210 px). */
export const NOTE_COLUMNS = 3;
export const NOTE_ROWS = 2;
/** The largest column or row the daemon stores. */
export const MAX_CELL = 255;

export interface Cell {
  column: number;
  row: number;
}

export interface GridSize {
  columns: number;
  rows: number;
}

export interface Placeable {
  id: number;
  column: number;
  row: number;
}

export interface Box {
  x: number;
  y: number;
  width: number;
  height: number;
}

export type Direction = 'up' | 'down' | 'left' | 'right';

export function gridSize(workspace: { width: number; height: number }): GridSize {
  return {
    columns: Math.max(1, Math.floor((workspace.width - 2 * GRID_PADDING) / CELL_WIDTH)),
    rows: Math.max(1, Math.floor((workspace.height - 2 * GRID_PADDING) / CELL_HEIGHT)),
  };
}

/** Top left corner of a cell in workspace pixels. */
export function cellOrigin(cell: Cell): { x: number; y: number } {
  return { x: GRID_PADDING + cell.column * CELL_WIDTH, y: GRID_PADDING + cell.row * CELL_HEIGHT };
}

/** The cell whose top left corner is nearest to a point, within the stored range. */
export function nearestCell(point: { x: number; y: number }): Cell {
  return {
    column: clamp(Math.round((point.x - GRID_PADDING) / CELL_WIDTH), 0, MAX_CELL),
    row: clamp(Math.round((point.y - GRID_PADDING) / CELL_HEIGHT), 0, MAX_CELL),
  };
}

/** The cell that contains a point. */
export function cellAt(point: { x: number; y: number }): Cell {
  return {
    column: clamp(Math.floor((point.x - GRID_PADDING) / CELL_WIDTH), 0, MAX_CELL),
    row: clamp(Math.floor((point.y - GRID_PADDING) / CELL_HEIGHT), 0, MAX_CELL),
  };
}

function clamp(value: number, min: number, max: number): number {
  return Math.min(Math.max(value, min), max);
}

function keyOf(cell: Cell): string {
  return `${String(cell.column)}:${String(cell.row)}`;
}

/** Column by column, top to bottom, like icons flow on a classic desktop. */
export function compareCells(a: Cell, b: Cell): number {
  return a.column - b.column || a.row - b.row;
}

/**
 * The next free cell at or after `start` in column order. Past the last visible column the grid grows to the right,
 * so every item gets a cell even when the screen is full.
 */
function nextFree(taken: Set<string>, size: GridSize, start: Cell): Cell {
  let column = start.column;
  let row = start.row;
  for (;;) {
    const cell = { column, row };
    if (!taken.has(keyOf(cell))) {
      return cell;
    }
    row += 1;
    if (row >= size.rows) {
      row = 0;
      column += 1;
    }
  }
}

/**
 * Where each icon shows on a grid of this size: in its own cell if that is on the screen and not taken by an icon
 * earlier in column order, otherwise in the next free cell from the nearest visible one.
 */
export function placeIcons(items: readonly Placeable[], size: GridSize): Map<number, Cell> {
  const sorted = [...items].sort((a, b) => compareCells(a, b) || a.id - b.id);
  const placed = new Map<number, Cell>();
  const taken = new Set<string>();
  const displaced: Placeable[] = [];
  for (const item of sorted) {
    const fits = item.column < size.columns && item.row < size.rows;
    if (fits && !taken.has(keyOf(item))) {
      placed.set(item.id, { column: item.column, row: item.row });
      taken.add(keyOf(item));
    } else {
      displaced.push(item);
    }
  }
  for (const item of displaced) {
    const start = {
      column: Math.min(item.column, size.columns - 1),
      row: Math.min(item.row, size.rows - 1),
    };
    const cell = nextFree(taken, size, start);
    placed.set(item.id, cell);
    taken.add(keyOf(cell));
  }
  return placed;
}

/** The first free cell in column order, for a new item. */
export function firstFreeCell(placed: ReadonlyMap<number, Cell>, size: GridSize): Cell {
  return nextFree(new Set([...placed.values()].map(keyOf)), size, { column: 0, row: 0 });
}

/**
 * The free cell nearest to `target` within the visible grid, or the next free one after it when the grid is full.
 * Between cells at the same distance the same column wins, then below over above, then right over left.
 */
function nearestFree(taken: Set<string>, size: GridSize, target: Cell): Cell {
  let best: Cell | null = null;
  let bestRank: number[] = [];
  for (let column = 0; column < size.columns; column++) {
    for (let row = 0; row < size.rows; row++) {
      const cell = { column, row };
      if (taken.has(keyOf(cell))) {
        continue;
      }
      const across = column - target.column;
      const along = row - target.row;
      const rank = [
        across ** 2 + along ** 2,
        Math.abs(across),
        along < 0 ? 1 : 0,
        across < 0 ? 1 : 0,
      ];
      if (best === null || isLower(rank, bestRank)) {
        best = cell;
        bestRank = rank;
      }
    }
  }
  return best ?? nextFree(taken, size, target);
}

function isLower(a: readonly number[], b: readonly number[]): boolean {
  const index = a.findIndex((value, position) => value !== b[position]);
  return index !== -1 && (a[index] ?? 0) < (b[index] ?? 0);
}

/**
 * Moves icons by a number of cells, as a drag or the keyboard does. Every moved icon keeps the distance to the others
 * where it can; one that would land outside the screen or on another icon goes to the nearest free cell.
 * Returns the new cells of the moved icons.
 */
export function moveIcons(
  placed: ReadonlyMap<number, Cell>,
  ids: readonly number[],
  delta: Cell,
  size: GridSize,
): Map<number, Cell> {
  const moving = new Set(ids);
  const taken = new Set(
    [...placed].filter(([id]) => !moving.has(id)).map(([, cell]) => keyOf(cell)),
  );
  const targets = ids
    .flatMap((id) => {
      const cell = placed.get(id);
      return cell === undefined
        ? []
        : [
            {
              id,
              cell: {
                column: clamp(cell.column + delta.column, 0, size.columns - 1),
                row: clamp(cell.row + delta.row, 0, size.rows - 1),
              },
            },
          ];
    })
    .sort((a, b) => compareCells(a.cell, b.cell) || a.id - b.id);
  const moved = new Map<number, Cell>();
  for (const { id, cell } of targets) {
    const free = taken.has(keyOf(cell)) ? nearestFree(taken, size, cell) : cell;
    moved.set(id, free);
    taken.add(keyOf(free));
  }
  return moved;
}

/** Where a note shows: its cell, moved in so the whole note stays on the screen. */
export function placeNote(cell: Cell, size: GridSize): Cell {
  return {
    column: clamp(cell.column, 0, Math.max(0, size.columns - NOTE_COLUMNS)),
    row: clamp(cell.row, 0, Math.max(0, size.rows - NOTE_ROWS)),
  };
}

/** Icons whose cell overlaps a rectangle in workspace pixels (the selection frame). */
export function iconsInBox(placed: ReadonlyMap<number, Cell>, box: Box): number[] {
  return [...placed]
    .filter(([, cell]) => {
      const origin = cellOrigin(cell);
      return (
        origin.x < box.x + box.width &&
        origin.x + CELL_WIDTH > box.x &&
        origin.y < box.y + box.height &&
        origin.y + CELL_HEIGHT > box.y
      );
    })
    .map(([id]) => id);
}

/**
 * The icon the arrow keys go to: the nearest one in that direction, preferring the same column (up and down) or the
 * same row (left and right). Null when there is none.
 */
export function neighbor(
  placed: ReadonlyMap<number, Cell>,
  from: number,
  direction: Direction,
): number | null {
  const origin = placed.get(from);
  if (origin === undefined) {
    return null;
  }
  const vertical = direction === 'up' || direction === 'down';
  const sign = direction === 'down' || direction === 'right' ? 1 : -1;
  let best: number | null = null;
  let bestScore = Infinity;
  for (const [id, cell] of placed) {
    const along = vertical ? cell.row - origin.row : cell.column - origin.column;
    const across = vertical ? cell.column - origin.column : cell.row - origin.row;
    if (id === from || along * sign <= 0) {
      continue;
    }
    // Staying in line matters more than distance: two cells across weigh like five along.
    const score = Math.abs(along) + 2.5 * Math.abs(across);
    if (score < bestScore) {
      best = id;
      bestScore = score;
    }
  }
  return best;
}
