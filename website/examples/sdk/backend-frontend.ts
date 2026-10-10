// The window side of a chat: JSON over app.backend.fetch, an avatar URL with a token, and a WebSocket with a ticket.
// pithyx.json declares "backend": { "endpoints": { "api": { "service": "server", "port": 8080 } }, ... }
import { isProblem, PithyxError, type PithyxApp } from '@pithyx/sdk';

export interface Message {
  id: number;
  user: string;
  text: string;
  sentAt: string;
}

/** Turns an answer that is not 2xx into a PithyxError; the proxy's own failures are RFC 9457 problems. */
async function failure(response: Response): Promise<PithyxError> {
  const body: unknown = await response.json().catch(() => null);
  return new PithyxError(
    isProblem(body)
      ? body
      : { code: 'app.backend.unavailable', params: {}, detail: `HTTP ${String(response.status)}` },
  );
}

export async function loadMessages(app: PithyxApp): Promise<Message[]> {
  const response = await app.backend.fetch('/messages', {
    headers: { Accept: 'application/json' },
  });
  if (!response.ok) {
    throw await failure(response);
  }
  return (await response.json()) as Message[];
}

export async function sendMessage(app: PithyxApp, text: string): Promise<Message> {
  const response = await app.backend.fetch('/messages', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ text }),
  });
  if (!response.ok) {
    throw await failure(response);
  }
  return (await response.json()) as Message;
}

/** An <img> source with ?pithyx-token=; it works as long as its token, so set it again after a few minutes. */
export async function showAvatar(app: PithyxApp, image: HTMLImageElement, user: string) {
  image.src = await app.backend.url(`/avatars/${encodeURIComponent(user)}`);
}

/**
 * Live messages. Every socket() call fetches a new one-time ticket, so a reconnect is just another call. While the
 * backend is updating or unavailable it waits for backendStatus; a stopped onDemand backend wakes for the request.
 */
export function followMessages(app: PithyxApp, onMessage: (message: Message) => void): () => void {
  let socket: WebSocket | null = null;
  let timer: ReturnType<typeof setTimeout> | undefined;
  let connecting = false;
  let stopped = false;
  let delay = 1000;

  async function open() {
    clearTimeout(timer);
    const status = app.context.backend?.status;
    if (
      stopped ||
      connecting ||
      socket !== null ||
      status === 'updating' ||
      status === 'unavailable'
    ) {
      return;
    }
    connecting = true;
    try {
      socket = await app.backend.socket('/live');
    } catch (error) {
      app.log.warn('No live connection', {
        code: error instanceof PithyxError ? error.code : 'unknown',
      });
      retry();
      return;
    } finally {
      connecting = false;
    }
    socket.addEventListener('open', () => {
      delay = 1000;
    });
    socket.addEventListener('message', (event: MessageEvent<string>) => {
      onMessage(JSON.parse(event.data) as Message);
    });
    socket.addEventListener('close', retry);
  }

  function retry() {
    socket = null;
    if (!stopped) {
      timer = setTimeout(() => void open(), delay);
      delay = Math.min(delay * 2, 30_000);
    }
  }

  const stopStatus = app.on('backendStatus', ({ status }) => {
    if (status === 'running') {
      void open();
    }
  });
  void open();
  return () => {
    stopped = true;
    clearTimeout(timer);
    stopStatus();
    socket?.close();
  };
}
