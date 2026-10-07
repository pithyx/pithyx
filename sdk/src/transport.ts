/**
 * The two transports. Both deliver the same protocol messages asynchronously and as structured clones, so an app
 * behaves the same whether it runs as a core app inside the desktop or sandboxed in an iframe.
 */

/** One end of a connection. */
export interface Endpoint {
  /** Sends a message to the other end. */
  post(message: unknown): void;
  /** Registers the single message handler of this end. */
  listen(handler: (message: unknown) => void): void;
  /** Closes this end; nothing is sent or received after it. */
  close(): void;
}

/** An end backed by a `MessagePort` (third-party apps in sandboxed iframes). */
export function portEndpoint(port: MessagePort): Endpoint {
  return {
    post: (message) => {
      port.postMessage(message);
    },
    listen: (handler) => {
      port.onmessage = (event: MessageEvent) => {
        handler(event.data);
      };
    },
    close: () => {
      port.onmessage = null;
      port.close();
    },
  };
}

/**
 * A pair of connected ends inside one page (core apps). Messages are cloned with `structuredClone` and delivered in
 * a later microtask, exactly like a `MessagePort` would, so nothing works in-process that would fail in an iframe.
 */
export function inProcessPair(): [Endpoint, Endpoint] {
  let closed = false;
  const handlers: [((message: unknown) => void) | null, ((message: unknown) => void) | null] = [
    null,
    null,
  ];
  const end = (self: 0 | 1): Endpoint => ({
    post: (message) => {
      if (closed) {
        return;
      }
      const copy = structuredClone(message);
      // Like a port, a message posted before the close still arrives.
      queueMicrotask(() => {
        handlers[self === 0 ? 1 : 0]?.(copy);
      });
    },
    listen: (handler) => {
      handlers[self] = handler;
    },
    close: () => {
      closed = true;
    },
  });
  return [end(0), end(1)];
}
