import type { components } from '@pithyx/api-client';
import { describe, expect, it } from 'vitest';
import { applyEvent, patternOf, reconnectDelay } from './reducers';

type Disk = components['schemas']['Disk'];
type Job = components['schemas']['Job'];
type Notification = components['schemas']['Notification'];

function notification(id: number, readAt: string | null = null): Notification {
  return {
    id,
    code: 'jobs.job.succeeded',
    params: {},
    tone: 'success',
    app: null,
    createdAt: '2026-09-29T10:00:00Z',
    readAt,
  };
}

function job(id: number, state: Job['state']): Job {
  return {
    id,
    kind: 'files.copy',
    state,
    progress: { itemsDone: 0, itemsTotal: 0, bytesDone: 0, bytesTotal: 0, currentItem: null },
    parameters: null,
    result: null,
    errorCode: null,
    errorParams: null,
    requestId: 'r',
    createdAt: '2026-09-29T10:00:00Z',
    startedAt: null,
    finishedAt: null,
  };
}

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

describe('notifications events', () => {
  const snapshot = {
    items: [notification(3), notification(2), notification(1, '2026-09-29T09:00:00Z')],
  };

  it('puts a new notification first and drops the ones the daemon dropped', () => {
    const next = applyEvent('notifications', snapshot, {
      change: 'created',
      notification: notification(4),
      ids: [1],
      readAt: null,
    });

    expect(next.items.map((item) => item.id)).toEqual([4, 3, 2]);
  });

  it('does not add a notification twice', () => {
    const once = applyEvent('notifications', undefined, {
      change: 'created',
      notification: notification(5),
      ids: [],
      readAt: null,
    });
    const twice = applyEvent('notifications', once, {
      change: 'created',
      notification: notification(5),
      ids: [],
      readAt: null,
    });

    expect(twice.items.map((item) => item.id)).toEqual([5]);
  });

  it('marks the read ones and keeps an earlier read time', () => {
    const next = applyEvent('notifications', snapshot, {
      change: 'read',
      notification: null,
      ids: [3, 1],
      readAt: '2026-09-29T11:00:00Z',
    });

    expect(next.items.map((item) => item.readAt)).toEqual([
      '2026-09-29T11:00:00Z',
      null,
      '2026-09-29T09:00:00Z',
    ]);
  });

  it('removes deleted ones', () => {
    const next = applyEvent('notifications', snapshot, {
      change: 'deleted',
      notification: null,
      ids: [3, 2],
      readAt: null,
    });

    expect(next.items.map((item) => item.id)).toEqual([1]);
  });
});

describe('jobs events', () => {
  it('adds and updates running jobs in id order and removes finished ones', () => {
    const started = applyEvent('jobs', { jobs: [job(7, 'running')] }, job(5, 'queued'));
    const running = applyEvent('jobs', started, job(5, 'running'));
    const finished = applyEvent('jobs', running, job(7, 'cancelled'));

    expect(started.jobs.map((entry) => entry.id)).toEqual([5, 7]);
    expect(running.jobs.map((entry) => entry.state)).toEqual(['running', 'running']);
    expect(finished.jobs.map((entry) => entry.id)).toEqual([5]);
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

describe('topic families', () => {
  it('finds the pattern of a concrete topic', () => {
    expect(patternOf('storage/disks')).toBe('storage/disks');
    expect(patternOf('files/3/0.2a.1f')).toBe('files/+/+');
    expect(patternOf('jobs/12')).toBe('jobs/+');
    expect(patternOf('files/3')).toBeUndefined();
  });
});
