import { describe, expect, it } from 'vitest';
import type { ProcessInfo } from '../sdk/monitor';
import { defaultDirection, sortProcesses, visibleRows } from './processes';

const processes: ProcessInfo[] = [
  { pid: 10, name: 'pithyxd', user: 'root', cpuPercent: 2, memoryBytes: 150 },
  { pid: 2, name: 'Samba', user: 'root', cpuPercent: null, memoryBytes: 90 },
  { pid: 30, name: 'bash', user: 'owner', cpuPercent: 2, memoryBytes: 5 },
  { pid: 4, name: 'zed', user: 'owner', cpuPercent: 30, memoryBytes: 20 },
];

describe('sortProcesses', () => {
  it('sorts by CPU with the busiest first, ties by pid, an unknown share as zero', () => {
    expect(sortProcesses(processes, 'cpu', 'descending', '', 'en').map((p) => p.pid)).toEqual([
      4, 10, 30, 2,
    ]);
  });

  it('sorts names without regard to case', () => {
    expect(sortProcesses(processes, 'name', 'ascending', '', 'en').map((p) => p.name)).toEqual([
      'bash',
      'pithyxd',
      'Samba',
      'zed',
    ]);
  });

  it('filters by name or user', () => {
    expect(sortProcesses(processes, 'pid', 'ascending', 'OWN', 'en').map((p) => p.pid)).toEqual([
      4, 30,
    ]);
    expect(sortProcesses(processes, 'memory', 'descending', 'samba', 'en')).toHaveLength(1);
  });

  it('starts numbers high and names low', () => {
    expect(defaultDirection('memory')).toBe('descending');
    expect(defaultDirection('user')).toBe('ascending');
  });
});

describe('visibleRows', () => {
  it('renders the rows in view with a margin, within the list', () => {
    expect(visibleRows(1000, 32, 0, 320)).toEqual({ first: 0, last: 20 });
    expect(visibleRows(1000, 32, 3200, 320)).toEqual({ first: 90, last: 120 });
    expect(visibleRows(15, 32, 0, 320)).toEqual({ first: 0, last: 15 });
    expect(visibleRows(0, 32, 500, 320)).toEqual({ first: 5, last: 5 });
  });
});
