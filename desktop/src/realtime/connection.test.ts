import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useErrorStore } from '../errors/errorStore';
import { realtime } from './connection';

class FakeSocket {
  static readonly OPEN = 1;
  static last: FakeSocket | undefined;
  readyState = 0;
  readonly sent: { type: string; id: string }[] = [];
  onopen: (() => void) | null = null;
  onmessage: ((event: { data: string }) => void) | null = null;
  onclose: ((event: { code: number }) => void) | null = null;

  constructor() {
    FakeSocket.last = this;
  }

  send(data: string) {
    this.sent.push(JSON.parse(data) as { type: string; id: string });
  }

  close() {
    this.readyState = 3;
  }

  open() {
    this.readyState = FakeSocket.OPEN;
    this.onopen?.();
  }

  receive(message: object) {
    this.onmessage?.({ data: JSON.stringify(message) });
  }
}

function connect(): FakeSocket {
  realtime.connect(() => undefined);
  const socket = FakeSocket.last;
  if (socket === undefined) {
    throw new Error('no socket');
  }
  socket.open();
  return socket;
}

describe('server errors', () => {
  beforeEach(() => {
    vi.stubGlobal('WebSocket', FakeSocket);
    vi.stubGlobal('location', { protocol: 'http:', host: 'localhost' });
    useErrorStore.setState({ current: null });
  });

  afterEach(() => {
    realtime.disconnect();
    vi.unstubAllGlobals();
  });

  it('shows them in the error dialog', () => {
    const socket = connect();
    socket.receive({
      type: 'error',
      id: 'c999',
      code: 'realtime.topic.unknown',
      params: {},
      requestId: 'r',
    });
    expect(useErrorStore.getState().current?.code).toBe('realtime.topic.unknown');
  });
});

describe('listen', () => {
  beforeEach(() => {
    vi.stubGlobal('WebSocket', FakeSocket);
    vi.stubGlobal('location', { protocol: 'http:', host: 'localhost' });
  });

  afterEach(() => {
    realtime.disconnect();
    vi.unstubAllGlobals();
  });

  it('gives a listener that joins a subscribed topic its current state', async () => {
    const socket = connect();
    const first: unknown[] = [];
    const stopFirst = realtime.listen('apps/com.example.chat/context', (data) => first.push(data));
    socket.receive({
      type: 'snap',
      topic: 'apps/com.example.chat/context',
      data: { version: '1.0.0' },
    });
    socket.receive({
      type: 'event',
      topic: 'apps/com.example.chat/context',
      data: { version: '1.0.1' },
    });

    const second: unknown[] = [];
    const stopSecond = realtime.listen('apps/com.example.chat/context', (data) =>
      second.push(data),
    );
    await Promise.resolve();

    expect(first).toEqual([{ version: '1.0.0' }, { version: '1.0.1' }]);
    expect(second).toEqual([{ version: '1.0.1' }]);
    expect(socket.sent.filter((message) => message.type === 'sub')).toHaveLength(1);
    stopFirst();
    stopSecond();
  });

  it('waits for the snapshot when nothing has arrived yet', async () => {
    const socket = connect();
    const seen: unknown[] = [];
    const stop = realtime.listen('apps/com.example.chat/context', (data) => seen.push(data));
    await Promise.resolve();
    expect(seen).toEqual([]);
    socket.receive({
      type: 'snap',
      topic: 'apps/com.example.chat/context',
      data: { version: '1.0.0' },
    });
    expect(seen).toEqual([{ version: '1.0.0' }]);
    stop();
  });

  it('gives nothing to a listener that left before the state was handed over', async () => {
    const socket = connect();
    const stopFirst = realtime.listen('apps/com.example.chat/context', () => undefined);
    socket.receive({
      type: 'snap',
      topic: 'apps/com.example.chat/context',
      data: { version: '1.0.0' },
    });
    const seen: unknown[] = [];
    const stop = realtime.listen('apps/com.example.chat/context', (data) => seen.push(data));
    stop();
    await Promise.resolve();
    expect(seen).toEqual([]);
    stopFirst();
  });
});
