import type { components } from '@vyxis/api-client';
import { describe, expect, it } from 'vitest';
import { applyEvent, reconnectDelay } from './reducers';

type Disk = components['schemas']['Disk'];

function disk(name: string, sizeBytes = 1000): Disk {
  return {
    id: `serial-${name}`,
    name,
    path: `/dev/${name}`,
    serial: name,
    wwn: null,
    model: null,
    vendor: null,
    sizeBytes,
    rotational: false,
    transport: null,
    removable: false,
    readOnly: false,
    partitions: [],
  };
}

describe('storage/disks events', () => {
  it('removes, replaces and adds disks and keeps them sorted by name', () => {
    const snapshot = { disks: [disk('sda'), disk('sdb'), disk('sdc')] };

    const next = applyEvent('storage/disks', snapshot, {
      upserts: [disk('sdb', 2000), disk('sdaa')],
      removals: ['serial-sdc'],
    });

    expect(next.disks.map((d) => d.name)).toEqual(['sda', 'sdaa', 'sdb']);
    expect(next.disks.find((d) => d.name === 'sdb')?.sizeBytes).toBe(2000);
  });

  it('sorts numbered devices naturally', () => {
    const next = applyEvent('storage/disks', undefined, {
      upserts: [disk('nvme10n1'), disk('nvme2n1')],
      removals: [],
    });

    expect(next.disks.map((d) => d.name)).toEqual(['nvme2n1', 'nvme10n1']);
  });
});

describe('reconnectDelay', () => {
  it('doubles up to thirty seconds', () => {
    expect([0, 1, 2, 10].map((attempt) => reconnectDelay(attempt, 1))).toEqual([
      500, 1000, 2000, 30_000,
    ]);
  });

  it('adds jitter down to half the delay', () => {
    expect(reconnectDelay(3, 0)).toBe(2000);
  });
});
