import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useErrorStore } from '../errors/errorStore';
import { useToasts } from '../shell/toasts';
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

function lastLayout(socket: FakeSocket) {
  const message = socket.sent.filter((entry) => entry.type === 'layout').at(-1);
  if (message === undefined) {
    throw new Error('no layout sent');
  }
  return message;
}

describe('layout saves', () => {
  beforeEach(() => {
    vi.stubGlobal('WebSocket', FakeSocket);
    vi.stubGlobal('location', { protocol: 'http:', host: 'localhost' });
    useToasts.setState({ items: [] });
    useErrorStore.setState({ current: null });
  });

  afterEach(() => {
    realtime.disconnect();
    vi.unstubAllGlobals();
  });

  it('reports failed saves once as a notice, not as the error dialog', () => {
    const socket = connect();
    for (let attempt = 0; attempt < 3; attempt++) {
      realtime.saveLayout({ version: 1, windows: [] });
      const { id } = lastLayout(socket);
      socket.receive({
        type: 'error',
        id,
        code: 'desktop.layout.too_large',
        params: {},
        requestId: 'r',
      });
    }
    expect(useErrorStore.getState().current).toBeNull();
    expect(useToasts.getState().items.map((item) => item.message.code)).toEqual([
      'desktop.saveFailed',
    ]);

    realtime.saveLayout({ version: 1, windows: [] });
    socket.receive({ type: 'ack', id: lastLayout(socket).id, requestId: 'r' });
    realtime.saveLayout({ version: 1, windows: [] });
    socket.receive({
      type: 'error',
      id: lastLayout(socket).id,
      code: 'system.internal.unexpected',
      params: {},
      requestId: 'r',
    });
    expect(useToasts.getState().items).toHaveLength(2);
  });

  it('keeps other errors in the error dialog', () => {
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

  it('resolves layoutSaved once the save is acknowledged', async () => {
    const socket = connect();
    realtime.saveLayout({ version: 1, windows: [] });
    let saved = false;
    const waiting = realtime.layoutSaved().then(() => {
      saved = true;
    });
    await Promise.resolve();
    expect(saved).toBe(false);
    socket.receive({ type: 'ack', id: lastLayout(socket).id, requestId: 'r' });
    await waiting;
    expect(saved).toBe(true);
  });

  it('sends a save made while connecting as soon as the socket opens', () => {
    realtime.connect(() => undefined);
    const socket = FakeSocket.last;
    realtime.saveLayout({ version: 1, windows: [] });
    expect(socket?.sent).toEqual([]);
    socket?.open();
    expect(socket?.sent.map((entry) => entry.type)).toEqual(['layout']);
  });
});
