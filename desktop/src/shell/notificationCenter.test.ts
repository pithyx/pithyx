import type { components } from '@pithyx/api-client';
import { describe, expect, it } from 'vitest';
import { jobProgress, kindKey, sourceApp } from './NotificationCenter';
import { createdNotification } from './notificationFeed';

type Job = components['schemas']['Job'];
type Notification = components['schemas']['Notification'];

const created: Notification = {
  id: 9,
  code: 'app.message',
  params: {},
  tone: 'info',
  app: 'org.pithyx.demo',
  createdAt: '2026-09-29T10:00:00Z',
  readAt: null,
};

function job(state: Job['state'], progress: Partial<Job['progress']> = {}): Job {
  return {
    id: 1,
    kind: 'files.copy',
    state,
    progress: {
      itemsDone: 0,
      itemsTotal: 0,
      bytesDone: 0,
      bytesTotal: 0,
      currentItem: null,
      ...progress,
    },
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

describe('toasts for notifications', () => {
  it('come only from created events, never from a snapshot', () => {
    expect(
      createdNotification({ change: 'created', notification: created, ids: [], readAt: null }),
    ).toBe(created);
    expect(createdNotification({ items: [created] })).toBeUndefined();
    expect(
      createdNotification({ change: 'read', notification: null, ids: [9], readAt: null }),
    ).toBeUndefined();
    expect(
      createdNotification({ change: 'deleted', notification: null, ids: [9], readAt: null }),
    ).toBeUndefined();
  });
});

describe('the app of a notification', () => {
  it('is the sender, or the core app of a job or storage alert', () => {
    expect(sourceApp(created)).toBe('org.pithyx.demo');
    expect(sourceApp({ app: null, code: 'jobs.job.failed', params: { kind: 'files.move' } })).toBe(
      'org.pithyx.files',
    );
    expect(
      sourceApp({ app: null, code: 'jobs.job.succeeded', params: { kind: 'storage.pool.create' } }),
    ).toBe('org.pithyx.storage');
    expect(sourceApp({ app: null, code: 'storage.alert.resolved', params: {} })).toBe(
      'org.pithyx.storage',
    );
    expect(
      sourceApp({ app: null, code: 'jobs.job.succeeded', params: { kind: 'test.work' } }),
    ).toBe(undefined);
  });
});

describe('running jobs', () => {
  it('have keys without dots inside the kind', () => {
    expect(kindKey('files.empty_trash')).toBe('kinds.files_empty_trash');
    expect(kindKey('storage.pool.import.readonly')).toBe('kinds.storage_pool_import_readonly');
  });

  it('show their progress by bytes, then by items, and none while unknown or queued', () => {
    expect(jobProgress(job('queued', { bytesTotal: 10 }), 'Copy')).toEqual({
      message: { code: 'running.queued', params: { kind: 'Copy' } },
      progress: null,
    });
    expect(
      jobProgress(
        job('running', { bytesDone: 25, bytesTotal: 100, itemsDone: 1, itemsTotal: 2 }),
        'Copy',
      ),
    ).toEqual({
      message: {
        code: 'running.bytes',
        params: { kind: 'Copy', done: { bytes: 25 }, total: { bytes: 100 } },
      },
      progress: 25,
    });
    expect(jobProgress(job('running', { itemsDone: 3, itemsTotal: 4 }), 'Copy')).toEqual({
      message: { code: 'running.items', params: { kind: 'Copy', done: 3, total: 4 } },
      progress: 75,
    });
    expect(jobProgress(job('running'), 'Copy').progress).toBeNull();
  });
});
