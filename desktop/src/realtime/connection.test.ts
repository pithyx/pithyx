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
