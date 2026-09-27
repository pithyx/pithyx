import {
  closeAction,
  type ServerMessage,
  type TopicEvents,
  type TopicName,
  type TopicSnapshots,
} from '@vyxis/api-client';
import { create } from 'zustand';
import { useErrorStore } from '../errors/errorStore';
import { notify } from '../shell/toasts';
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
  private attempt = 0;
  private retryTimer: ReturnType<typeof setTimeout> | undefined;
  private nextId = 0;
  private onSessionEnded: () => void = () => undefined;
  private pendingLayout: unknown = undefined;
  private readonly layoutMessages = new Set<string>();
  private layoutFailureShown = false;
  private layoutWaiters: (() => void)[] = [];

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
    this.pendingLayout = undefined;
    this.layoutMessages.clear();
    this.layoutFailureShown = false;
    this.settleLayout();
    useRealtime.setState({ status: 'idle', data: {} });
  }

  /** Saves the desktop layout of this browser; sent now or as soon as the connection is open. The latest wins. */
  saveLayout(layout: unknown): void {
    this.pendingLayout = layout;
    this.flushLayout();
  }

  /** Resolves once every layout handed to saveLayout was answered, or when nothing more can be sent. */
  layoutSaved(): Promise<void> {
    return new Promise((resolve) => {
      this.layoutWaiters.push(resolve);
      this.settleLayout();
    });
  }

  private settleLayout(): void {
    if (
      (this.pendingLayout === undefined || this.socket === null) &&
      this.layoutMessages.size === 0
    ) {
      for (const resolve of this.layoutWaiters.splice(0)) {
        resolve();
      }
    }
  }

  private flushLayout(): void {
    if (this.pendingLayout !== undefined && this.socket?.readyState === WebSocket.OPEN) {
      const id = `c${String(++this.nextId)}`;
      this.layoutMessages.add(id);
      this.socket.send(JSON.stringify({ type: 'layout', id, data: this.pendingLayout }));
      this.pendingLayout = undefined;
    }
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
      this.flushLayout();
    };
    socket.onmessage = (event: MessageEvent<string>) => {
      this.handle(JSON.parse(event.data) as ServerMessage);
    };
    socket.onclose = (event) => {
      if (this.socket !== socket) {
        return;
      }
      this.socket = null;
      // Saves in flight on this socket are never answered; a pending one is sent again after reconnecting.
      this.layoutMessages.clear();
      this.settleLayout();
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
        useRealtime.setState((state) => ({
          data: { ...state.data, [message.topic]: message.data },
        }));
        break;
      case 'event': {
        const topic = message.topic as TopicName;
        if (!this.references.has(topic)) {
          break;
        }
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
        // A failed background save is no reason for a modal dialog after every window move: tell once.
        if (this.layoutMessages.delete(message.id)) {
          if (!this.layoutFailureShown) {
            this.layoutFailureShown = true;
            notify({ code: 'desktop.saveFailed', params: { requestId: message.requestId } });
          }
          this.settleLayout();
          break;
        }
        useErrorStore.setState({
          current: { code: message.code, params: message.params, requestId: message.requestId },
        });
        break;
      case 'ack':
        if (this.layoutMessages.delete(message.id)) {
          this.layoutFailureShown = false;
          this.settleLayout();
        }
        break;
    }
  }

  private send(type: 'sub' | 'unsub', topic: TopicName): void {
    if (this.socket?.readyState === WebSocket.OPEN) {
      this.socket.send(JSON.stringify({ type, id: `c${String(++this.nextId)}`, topic }));
    }
  }
}

export const realtime = new RealtimeConnection();
