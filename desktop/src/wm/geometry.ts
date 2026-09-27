import type { Point, Rect, Size, SnapZone } from './types';

/** Matches --titlebar-height in the theme tokens. */
export const TITLEBAR_HEIGHT = 40;
/** Offset between windows opened one after another. */
export const CASCADE_STEP = 32;
/** How much of a title bar stays inside the workspace horizontally, so a window can always be dragged back. */
export const MIN_VISIBLE = 96;
/** Distance from a workspace edge that triggers a snap zone while dragging. */
export const SNAP_EDGE = 12;
/** Workspaces at least this wide offer thirds instead of halves. */
export const ULTRAWIDE_WIDTH = 2400;

export type Edge = 'n' | 's' | 'e' | 'w' | 'ne' | 'nw' | 'se' | 'sw';

export function isUltrawide(workspace: Size): boolean {
  return workspace.width >= ULTRAWIDE_WIDTH;
}

function clamp(value: number, min: number, max: number): number {
  return Math.min(Math.max(value, min), Math.max(min, max));
}

/** A size within the minimum size and the workspace. The workspace wins if it is smaller than the minimum. */
export function clampSize(size: Size, min: Size, workspace: Size): Size {
  return {
    width: Math.round(Math.min(Math.max(size.width, min.width), workspace.width)),
    height: Math.round(Math.min(Math.max(size.height, min.height), workspace.height)),
  };
}

/**
 * Keeps a window reachable: the size fits the workspace, the title bar stays fully inside vertically and at least
 * MIN_VISIBLE pixels of it inside horizontally.
 */
export function clampRect(rect: Rect, min: Size, workspace: Size): Rect {
  const size = clampSize(rect, min, workspace);
  const visible = Math.min(MIN_VISIBLE, size.width);
  return {
    x: Math.round(clamp(rect.x, visible - size.width, workspace.width - visible)),
    y: Math.round(clamp(rect.y, 0, workspace.height - TITLEBAR_HEIGHT)),
    ...size,
  };
}

/**
 * Where a new window opens: centered if it is the first, otherwise one step down and right of the previous one,
 * back at the top left when that would leave the workspace.
 */
export function cascade(previous: Rect | null, size: Size, workspace: Size): Rect {
  if (previous === null) {
    return {
      x: Math.round(Math.max(0, (workspace.width - size.width) / 2)),
      y: Math.round(Math.max(0, (workspace.height - size.height) / 3)),
      ...size,
    };
  }
  const next = { x: previous.x + CASCADE_STEP, y: previous.y + CASCADE_STEP, ...size };
  if (next.x + size.width > workspace.width || next.y + size.height > workspace.height) {
    return { x: CASCADE_STEP, y: CASCADE_STEP, ...size };
  }
  return next;
}

/** The rect of a window moved by `delta` from where the drag started, kept reachable. */
export function dragRect(start: Rect, delta: Point, min: Size, workspace: Size): Rect {
  return clampRect({ ...start, x: start.x + delta.x, y: start.y + delta.y }, min, workspace);
}

/** The rect of a window resized from one edge or corner, never below its minimum size or past the workspace. */
export function resizeRect(
  start: Rect,
  edge: Edge,
  delta: Point,
  min: Size,
  workspace: Size,
): Rect {
  let { x, y, width, height } = start;
  const right = start.x + start.width;
  const bottom = start.y + start.height;
  // A window may already reach past an edge (only its title bar must stay reachable); resizing never pulls it in.
  if (edge.includes('e')) {
    width = clamp(
      start.width + delta.x,
      min.width,
      Math.max(start.width, workspace.width - start.x),
    );
  }
  if (edge.includes('w')) {
    x = clamp(start.x + delta.x, Math.min(0, start.x), right - min.width);
    width = right - x;
  }
  if (edge.includes('s')) {
    height = clamp(
      start.height + delta.y,
      min.height,
      Math.max(start.height, workspace.height - start.y),
    );
  }
  if (edge.startsWith('n')) {
    y = clamp(start.y + delta.y, 0, bottom - min.height);
    height = bottom - y;
  }
  return {
    x: Math.round(x),
    y: Math.round(y),
    width: Math.round(width),
    height: Math.round(height),
  };
}

/** The snap target under the pointer while dragging, if any. */
export function snapZoneAt(point: Point, workspace: Size): SnapZone | 'maximize' | null {
  const ultrawide = isUltrawide(workspace);
  if (point.y <= SNAP_EDGE) {
    const third = workspace.width / 3;
    return ultrawide && point.x >= third && point.x <= 2 * third ? 'center-third' : 'maximize';
  }
  if (point.x <= SNAP_EDGE) {
    return ultrawide ? 'left-third' : 'left';
  }
  if (point.x >= workspace.width - SNAP_EDGE) {
    return ultrawide ? 'right-third' : 'right';
  }
  return null;
}

/** The area a snap zone covers. */
export function zoneRect(zone: SnapZone | 'maximize', workspace: Size): Rect {
  const { width, height } = workspace;
  const half = Math.round(width / 2);
  const third = Math.round(width / 3);
  const twoThirds = Math.round((width * 2) / 3);
  switch (zone) {
    case 'maximize':
      return { x: 0, y: 0, width, height };
    case 'left':
      return { x: 0, y: 0, width: half, height };
    case 'right':
      return { x: half, y: 0, width: width - half, height };
    case 'left-third':
      return { x: 0, y: 0, width: third, height };
    case 'center-third':
      return { x: third, y: 0, width: twoThirds - third, height };
    case 'right-third':
      return { x: twoThirds, y: 0, width: width - twoThirds, height };
    case 'left-two-thirds':
      return { x: 0, y: 0, width: twoThirds, height };
    case 'right-two-thirds':
      return { x: third, y: 0, width: width - third, height };
  }
}

/** Medium screens only split in halves; thirds map to the nearest half and the center to maximized. */
export function mediumZone(zone: SnapZone): 'left' | 'right' | 'maximize' {
  if (zone === 'center-third') {
    return 'maximize';
  }
  return zone.startsWith('left') ? 'left' : 'right';
}

/**
 * The free rect to continue a drag with when a maximized or snapped window is pulled away: the window gets its
 * free size back and the pointer keeps its relative position along the title bar.
 */
export function restoreAnchor(free: Rect, current: Rect, pointer: Point): Rect {
  const ratio = current.width > 0 ? (pointer.x - current.x) / current.width : 0.5;
  return {
    x: Math.round(pointer.x - ratio * free.width),
    y: Math.round(current.y),
    width: free.width,
    height: free.height,
  };
}
