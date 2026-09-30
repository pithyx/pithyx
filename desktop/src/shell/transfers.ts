import { create } from 'zustand';

/** An upload or download of any app, shown in the taskbar until it ends. */
export interface Transfer {
  id: number;
  kind: 'upload' | 'download';
  name: string;
  loaded: number;
  /** 0 when the size is not known (a download without Content-Length). */
  total: number;
  /** Bytes per second over the last few seconds, 0 until measured. */
  speed: number;
  failed: boolean;
}

interface TransferState {
  transfers: Transfer[];
}

/** Progress changes many times a second; the store takes at most this many updates per second. */
const UPDATES_PER_SECOND = 4;
/** How much of the speed each new measurement makes up (exponential moving average). */
const SPEED_WEIGHT = 0.3;

export const useTransfers = create<TransferState>(() => ({ transfers: [] }));

const cancels = new Map<number, () => void>();
const measured = new Map<number, { at: number; loaded: number }>();
const pending = new Map<number, number>();
let flushTimer: ReturnType<typeof setTimeout> | null = null;
let nextId = 1;

function update(id: number, change: (transfer: Transfer) => Transfer) {
  useTransfers.setState((state) => ({
    transfers: state.transfers.map((transfer) =>
      transfer.id === id ? change(transfer) : transfer,
    ),
  }));
}

function flush() {
  flushTimer = null;
  const now = performance.now();
  for (const [id, loaded] of pending) {
    const last = measured.get(id);
    measured.set(id, { at: now, loaded });
    update(id, (transfer) => {
      if (last === undefined || now <= last.at) {
        return { ...transfer, loaded };
      }
      const current = ((loaded - last.loaded) * 1000) / (now - last.at);
      const speed =
        transfer.speed === 0 ? current : transfer.speed + SPEED_WEIGHT * (current - transfer.speed);
      return { ...transfer, loaded, speed };
    });
  }
  pending.clear();
}

/** Registers a transfer; `cancel` stops it (the transfer then ends through {@link endTransfer}). */
export function startTransfer(
  kind: Transfer['kind'],
  name: string,
  total: number,
  cancel: () => void,
): number {
  const id = nextId++;
  cancels.set(id, cancel);
  measured.set(id, { at: performance.now(), loaded: 0 });
  useTransfers.setState((state) => ({
    transfers: [...state.transfers, { id, kind, name, loaded: 0, total, speed: 0, failed: false }],
  }));
  return id;
}

export function reportProgress(id: number, loaded: number): void {
  pending.set(id, loaded);
  flushTimer ??= setTimeout(flush, 1000 / UPDATES_PER_SECOND);
}

/** Removes a finished or cancelled transfer; a failed one stays, marked, until it is dismissed. */
export function endTransfer(id: number, failed = false): void {
  pending.delete(id);
  measured.delete(id);
  cancels.delete(id);
  if (failed) {
    update(id, (transfer) => ({ ...transfer, failed: true, speed: 0 }));
  } else {
    dismissTransfer(id);
  }
}

export function cancelTransfer(id: number): void {
  cancels.get(id)?.();
}

export function dismissTransfer(id: number): void {
  useTransfers.setState((state) => ({
    transfers: state.transfers.filter((transfer) => transfer.id !== id),
  }));
}

/** Seconds until a transfer ends at its current speed, or null when that cannot be told yet. */
export function secondsLeft(transfer: Transfer): number | null {
  if (transfer.total === 0 || transfer.speed <= 0) {
    return null;
  }
  return Math.max(0, Math.ceil((transfer.total - transfer.loaded) / transfer.speed));
}

/**
 * Downloads bigger than this go to the browser's own download instead: the file is collected in a Blob before it is
 * saved, and browsers keep large Blobs on disk only up to a limit.
 */
export const DOWNLOAD_BLOB_LIMIT = 4 * 1024 ** 3;

/**
 * Downloads a file with progress in the taskbar and saves it under `name`. Returns once the download started; a
 * failed download stays in the taskbar, marked. Files above {@link DOWNLOAD_BLOB_LIMIT} use the browser's download.
 */
export function download(url: string, name: string): void {
  const controller = new AbortController();
  const id = startTransfer('download', name, 0, () => {
    controller.abort();
  });
  void (async () => {
    try {
      const response = await fetch(url, {
        signal: controller.signal,
        headers: { 'X-Pithyx-Request': '1' },
      });
      const total = Number(response.headers.get('Content-Length') ?? 0);
      if (!response.ok || response.body === null) {
        endTransfer(id, true);
        return;
      }
      if (total > DOWNLOAD_BLOB_LIMIT) {
        controller.abort();
        endTransfer(id);
        save(url, '');
        return;
      }
      update(id, (transfer) => ({ ...transfer, total }));
      const reader = response.body.getReader();
      const chunks: Uint8Array<ArrayBuffer>[] = [];
      let loaded = 0;
      for (;;) {
        const { done, value } = await reader.read();
        if (done) {
          break;
        }
        chunks.push(value);
        loaded += value.byteLength;
        reportProgress(id, loaded);
      }
      const blob = URL.createObjectURL(
        new Blob(chunks, { type: response.headers.get('Content-Type') ?? '' }),
      );
      save(blob, name);
      // The browser reads the Blob when the download starts; give it a minute before it is released.
      setTimeout(() => {
        URL.revokeObjectURL(blob);
      }, 60_000);
      endTransfer(id);
    } catch {
      // A cancelled download ends quietly; a broken connection stays in the list, marked.
      endTransfer(id, !controller.signal.aborted);
    }
  })();
}

function save(href: string, name: string) {
  const link = document.createElement('a');
  link.href = href;
  link.download = name;
  link.rel = 'noopener';
  document.body.append(link);
  link.click();
  link.remove();
}
