import { describe, expect, it } from 'vitest';
import {
  MIN_VISIBLE,
  SNAP_EDGE,
  clampRect,
  dragRect,
  mediumZone,
  resizeRect,
  restoreAnchor,
  snapZoneAt,
  zoneRect,
} from './geometry';

const laptop = { width: 1440, height: 848 };
const ultrawide = { width: 3440, height: 1388 };
const min = { width: 320, height: 200 };

describe('clampRect', () => {
  it('leaves a window inside the workspace untouched', () => {
    const rect = { x: 100, y: 50, width: 600, height: 400 };
    expect(clampRect(rect, min, laptop)).toEqual(rect);
  });

  it('keeps part of the title bar inside on every side', () => {
    expect(clampRect({ x: 1439, y: 900, width: 600, height: 400 }, min, laptop)).toEqual({
      x: laptop.width - MIN_VISIBLE,
      y: laptop.height - 40,
      width: 600,
      height: 400,
    });
    expect(clampRect({ x: -700, y: -20, width: 600, height: 400 }, min, laptop)).toMatchObject({
      x: MIN_VISIBLE - 600,
      y: 0,
    });
  });

  it('never makes a window smaller than its minimum or larger than the workspace', () => {
    expect(clampRect({ x: 0, y: 0, width: 10, height: 10 }, min, laptop)).toMatchObject(min);
    expect(clampRect({ x: 0, y: 0, width: 9000, height: 9000 }, min, laptop)).toMatchObject(laptop);
  });
});

describe('dragRect', () => {
  it('moves by the pointer delta', () => {
    const start = { x: 100, y: 100, width: 600, height: 400 };
    expect(dragRect(start, { x: 50, y: -30 }, min, laptop)).toEqual({ ...start, x: 150, y: 70 });
  });

  it('stops the title bar at the top edge', () => {
    const start = { x: 100, y: 100, width: 600, height: 400 };
    expect(dragRect(start, { x: 0, y: -500 }, min, laptop).y).toBe(0);
  });
});

describe('resizeRect', () => {
  const start = { x: 200, y: 150, width: 600, height: 400 };

  it('grows from the bottom right corner', () => {
    expect(resizeRect(start, 'se', { x: 100, y: 50 }, min, laptop)).toEqual({
      ...start,
      width: 700,
      height: 450,
    });
  });

  it('moves the left and top edges while keeping the opposite edges in place', () => {
    const rect = resizeRect(start, 'nw', { x: -50, y: -40 }, min, laptop);
    expect(rect).toEqual({ x: 150, y: 110, width: 650, height: 440 });
    expect(rect.x + rect.width).toBe(start.x + start.width);
    expect(rect.y + rect.height).toBe(start.y + start.height);
  });

  it('stops at the minimum size from any edge', () => {
    expect(resizeRect(start, 'w', { x: 500, y: 0 }, min, laptop)).toMatchObject({
      x: 200 + 600 - 320,
      width: 320,
    });
    expect(resizeRect(start, 'n', { x: 0, y: 500 }, min, laptop)).toMatchObject({
      y: 150 + 400 - 200,
      height: 200,
    });
    expect(resizeRect(start, 'e', { x: -500, y: 0 }, min, laptop).width).toBe(320);
    expect(resizeRect(start, 's', { x: 0, y: -500 }, min, laptop).height).toBe(200);
  });

  it('stops at the workspace edges', () => {
    expect(resizeRect(start, 'e', { x: 5000, y: 0 }, min, laptop).width).toBe(laptop.width - 200);
    expect(resizeRect(start, 'w', { x: -5000, y: 0 }, min, laptop)).toMatchObject({
      x: 0,
      width: 800,
    });
    expect(resizeRect(start, 'n', { x: 0, y: -5000 }, min, laptop)).toMatchObject({
      y: 0,
      height: 550,
    });
  });

  it('does not pull in a window that already reaches past the right or left edge', () => {
    const past = { x: laptop.width - 300, y: 100, width: 600, height: 400 };
    expect(resizeRect(past, 'e', { x: 16, y: 0 }, min, laptop).width).toBe(600);
    expect(resizeRect(past, 'se', { x: -16, y: 16 }, min, laptop)).toMatchObject({
      width: 584,
      height: 416,
    });
    const left = { x: -200, y: 100, width: 600, height: 400 };
    expect(resizeRect(left, 'w', { x: 10, y: 0 }, min, laptop)).toMatchObject({
      x: -190,
      width: 590,
    });
  });

  it('changes only the axis of an edge handle', () => {
    expect(resizeRect(start, 'e', { x: 10, y: 99 }, min, laptop)).toMatchObject({
      height: 400,
      y: 150,
    });
    expect(resizeRect(start, 's', { x: 99, y: 10 }, min, laptop)).toMatchObject({
      width: 600,
      x: 200,
    });
  });
});

describe('snapZoneAt', () => {
  it('offers halves and maximize on a laptop', () => {
    expect(snapZoneAt({ x: 0, y: 400 }, laptop)).toBe('left');
    expect(snapZoneAt({ x: SNAP_EDGE, y: 400 }, laptop)).toBe('left');
    expect(snapZoneAt({ x: laptop.width - 1, y: 400 }, laptop)).toBe('right');
    expect(snapZoneAt({ x: 700, y: 0 }, laptop)).toBe('maximize');
    expect(snapZoneAt({ x: 700, y: 400 }, laptop)).toBeNull();
    expect(snapZoneAt({ x: SNAP_EDGE + 1, y: 400 }, laptop)).toBeNull();
  });

  it('counts a pointer past the edge as the edge', () => {
    expect(snapZoneAt({ x: -40, y: 400 }, laptop)).toBe('left');
    expect(snapZoneAt({ x: 700, y: -10 }, laptop)).toBe('maximize');
  });

  it('offers thirds on ultrawide screens', () => {
    expect(snapZoneAt({ x: 0, y: 600 }, ultrawide)).toBe('left-third');
    expect(snapZoneAt({ x: ultrawide.width, y: 600 }, ultrawide)).toBe('right-third');
    expect(snapZoneAt({ x: 1720, y: 0 }, ultrawide)).toBe('center-third');
    expect(snapZoneAt({ x: 200, y: 0 }, ultrawide)).toBe('maximize');
  });
});

describe('zoneRect', () => {
  it('tiles the workspace without gaps', () => {
    const left = zoneRect('left', laptop);
    const right = zoneRect('right', laptop);
    expect(left.width + right.width).toBe(laptop.width);
    expect(right.x).toBe(left.width);
    const thirds = (['left-third', 'center-third', 'right-third'] as const).map((zone) =>
      zoneRect(zone, ultrawide),
    );
    expect(thirds.reduce((sum, rect) => sum + rect.width, 0)).toBe(ultrawide.width);
    expect(thirds[1]?.x).toBe(thirds[0]?.width);
    expect(
      zoneRect('left-two-thirds', ultrawide).width + zoneRect('right-third', ultrawide).width,
    ).toBe(ultrawide.width);
    expect(zoneRect('maximize', laptop)).toEqual({ x: 0, y: 0, ...laptop });
  });
});

describe('mediumZone', () => {
  it('maps thirds to halves on tablets', () => {
    expect(mediumZone('left-two-thirds')).toBe('left');
    expect(mediumZone('right-third')).toBe('right');
    expect(mediumZone('center-third')).toBe('maximize');
  });
});

describe('restoreAnchor', () => {
  it('keeps the pointer at the same relative position along the title bar', () => {
    const free = { x: 300, y: 200, width: 600, height: 400 };
    const maximized = { x: 0, y: 0, width: 1440, height: 848 };
    const rect = restoreAnchor(free, maximized, { x: 720, y: 20 });
    expect(rect).toEqual({ x: 420, y: 0, width: 600, height: 400 });
  });
});
