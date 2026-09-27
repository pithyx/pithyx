import {
  closeAction,
  type ServerMessage,
  type TopicEvents,
  type TopicName,
  type TopicSnapshots,
} from '@vyxis/api-client';
import { create } from 'zustand';
import { useErrorStore } from '../errors/errorStore';
import { applyEvent, reconnectDelay } from './reducers';

export type RealtimeStatus = 'idle' | 'connecting' | 'open' | 'reconnecting' | 'stopped';

type TopicData = { [K in TopicName]?: TopicSnapshots[K] };

interface RealtimeState {
  status: RealtimeStatus;
  data: TopicData;
}

/** Live topic data. Components read it with narrow selectors, for example through useTopic. */
export const useRealtime = create<RealtimeState>(() => ({ status: 'idle', data: {} }));

/**
 * The single WebSocket of this tab. Subscriptions are reference-counted and restored after every reconnect; what
 * happens after a close is decided by the daemon's close code (closeAction).
 */
class RealtimeConnection {
  private socket: WebSocket | null = null;
  private readonly references = new Map<TopicName, number>();
  private readonly listeners = new Map<string, Set<(data: unknown) => void>>();
  private attempt = 0;
  private retryTimer: ReturnType<typeof setTimeout> | undefined;
  private nextId = 0;
  private onSessionEnded: () => void = () => undefined;

  connect(onSessionEnded: () => void): void {
    this.onSessionEnded = onSessionEnded;
    if (this.socket === null) {
      this.open();
    }
  }

  disconnect(): void {
    clearTimeout(this.retryTimer);
    const socket = this.socket;
    this.socket = null;
    socket?.close(1000);
    this.references.clear();
    useRealtime.setState({ status: 'idle', data: {} });
  }

  /** Subscribes to a topic; call the returned function to release it. */
  subscribe(topic: TopicName): () => void {
    const count = this.references.get(topic) ?? 0;
    this.references.set(topic, count + 1);
    if (count === 0) {
      this.send('sub', topic);
    }
    return () => {
      this.release(topic);
    };
  }

  /**
   * Subscribes to one concrete topic of a family (a folder, a job) and hands its snapshot and every event to the
   * listener, which keeps its own state. Call the returned function to release it.
   */
  listen(topic: string, listener: (data: unknown) => void): () => void {
    const set = this.listeners.get(topic) ?? new Set();
    set.add(listener);
    this.listeners.set(topic, set);
    const release = this.subscribe(topic as TopicName);
    return () => {
      set.delete(listener);
      if (set.size === 0) {
        this.listeners.delete(topic);
      }
      release();
    };
  }

  private release(topic: TopicName): void {
    const count = (this.references.get(topic) ?? 1) - 1;
    if (count > 0) {
      this.references.set(topic, count);
      return;
    }
    this.references.delete(topic);
    this.send('unsub', topic);
    useRealtime.setState((state) => ({ data: { ...state.data, [topic]: undefined } }));
  }

  private open(): void {
    const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const socket = new WebSocket(`${protocol}//${location.host}/ws`);
    this.socket = socket;
    useRealtime.setState({ status: this.attempt === 0 ? 'connecting' : 'reconnecting' });

    socket.onopen = () => {
      this.attempt = 0;
      useRealtime.setState({ status: 'open' });
      for (const topic of this.references.keys()) {
        this.send('sub', topic);
      }
    };
    socket.onmessage = (event: MessageEvent<string>) => {
      this.handle(JSON.parse(event.data) as ServerMessage);
    };
    socket.onclose = (event) => {
      if (this.socket !== socket) {
        return;
      }
      this.socket = null;
      switch (closeAction(event.code)) {
        case 'reconnect':
          useRealtime.setState({ status: 'reconnecting' });
          this.retryTimer = setTimeout(() => {
            this.open();
          }, reconnectDelay(this.attempt++));
          break;
        case 'login':
          useRealtime.setState({ status: 'idle', data: {} });
          this.onSessionEnded();
          break;
        case 'stop':
          useRealtime.setState({ status: 'stopped' });
          break;
        case 'none':
          break;
      }
    };
  }

  private handle(message: ServerMessage): void {
    switch (message.type) {
      case 'snap':
        this.notify(message.topic, message.data);
        useRealtime.setState((state) => ({
          data: { ...state.data, [message.topic]: message.data },
        }));
        break;
      case 'event': {
        const topic = message.topic as TopicName;
        if (!this.references.has(topic)) {
          break;
        }
        this.notify(topic, message.data);
        useRealtime.setState((state) => ({
          data: {
            ...state.data,
            [topic]: applyEvent(
              topic,
              state.data[topic],
              message.data as TopicEvents[typeof topic],
            ),
          },
        }));
        break;
      }
      case 'error':
        useErrorStore.setState({
          current: { code: message.code, params: message.params, requestId: message.requestId },
        });
        break;
      case 'ack':
        break;
    }
  }

  private notify(topic: string, data: unknown): void {
    for (const listener of this.listeners.get(topic) ?? []) {
      listener(data);
    }
  }

  private send(type: 'sub' | 'unsub', topic: TopicName): void {
    if (this.socket?.readyState === WebSocket.OPEN) {
      this.socket.send(JSON.stringify({ type, id: `c${String(++this.nextId)}`, topic }));
    }
  }
}

export const realtime = new RealtimeConnection();
