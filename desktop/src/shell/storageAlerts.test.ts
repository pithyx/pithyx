import { describe, expect, it } from 'vitest';
import { alertMessage, bannerAlert, type StorageAlert } from './storageAlerts';

function alert(key: string, severity: string, since: string, muted = false): StorageAlert {
  return { key, severity, since, muted, code: 'storage.alert.poolDegraded', params: {} };
}

describe('bannerAlert', () => {
  it('shows the oldest critical alert the user has not muted', () => {
    const alerts = [
      alert('pool.capacity:a', 'warning', '2026-09-01T00:00:00Z'),
      alert('pool.degraded:b', 'critical', '2026-09-03T00:00:00Z'),
      alert('pool.faulted:c', 'critical', '2026-09-02T00:00:00Z', true),
      alert('disk.smart_failed:d', 'critical', '2026-09-02T12:00:00Z'),
    ];

    expect(bannerAlert(alerts)?.key).toBe('disk.smart_failed:d');
  });

  it('shows nothing for warnings or muted alerts only', () => {
    expect(
      bannerAlert([
        alert('pool.capacity:a', 'warning', '2026-09-01T00:00:00Z'),
        alert('pool.degraded:b', 'critical', '2026-09-01T00:00:00Z', true),
      ]),
    ).toBeUndefined();
  });
});

describe('alertMessage', () => {
  it('keeps string and number parameters', () => {
    const message = alertMessage({
      ...alert('pool.capacity:a', 'warning', '2026-09-01T00:00:00Z'),
      code: 'storage.alert.capacityWarning',
      params: { pool: 'a', percent: 85, nested: { no: 1 } } as unknown as StorageAlert['params'],
    });

    expect(message).toEqual({
      code: 'storage.alert.capacityWarning',
      params: { pool: 'a', percent: 85 },
    });
  });
});
