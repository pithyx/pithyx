import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  cancelTransfer,
  dismissTransfer,
  endTransfer,
  reportProgress,
  secondsLeft,
  startTransfer,
  useTransfers,
} from './transfers';

afterEach(() => {
  vi.useRealTimers();
  useTransfers.setState({ transfers: [] });
});

describe('transfers', () => {
  it('measure the speed and tell the time left', () => {
    vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout', 'performance', 'Date'] });
    const id = startTransfer('upload', 'a.bin', 10_000_000, () => undefined);
    vi.advanceTimersByTime(1000);
    reportProgress(id, 1_000_000);
    vi.advanceTimersByTime(250);
    const [transfer] = useTransfers.getState().transfers;
    if (transfer === undefined) {
      throw new Error('the transfer is missing');
    }
    expect(transfer.loaded).toBe(1_000_000);
    expect(transfer.speed).toBeGreaterThan(700_000);
    expect(transfer.speed).toBeLessThan(1_100_000);
    expect(secondsLeft(transfer)).toBeGreaterThan(8);
    expect(secondsLeft(transfer)).toBeLessThan(13);
  });

  it('cannot tell the time left without a size or a speed', () => {
    const id = startTransfer('download', 'b.bin', 0, () => undefined);
    const [transfer] = useTransfers.getState().transfers.filter((entry) => entry.id === id);
    expect(transfer && secondsLeft(transfer)).toBeNull();
  });

  it('cancel through the registered function, drop finished ones and keep failed ones', () => {
    const cancel = vi.fn();
    const done = startTransfer('upload', 'c.bin', 10, cancel);
    const broken = startTransfer('download', 'd.bin', 10, () => undefined);
    cancelTransfer(done);
    expect(cancel).toHaveBeenCalledOnce();
    endTransfer(done);
    endTransfer(broken, true);
    expect(useTransfers.getState().transfers).toEqual([
      expect.objectContaining({ id: broken, failed: true }),
    ]);
    dismissTransfer(broken);
    expect(useTransfers.getState().transfers).toEqual([]);
  });
});
