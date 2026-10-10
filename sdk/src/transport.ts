/**
 * The two transports. Both deliver the same protocol messages asynchronously and as structured clones, so an app
 * behaves the same whether it runs as a core app inside the desktop or sandboxed in an iframe.
 */

/** One end of a connection. */
export interface Endpoint {
  /**
   * Sends a message to the other end.
   *
   * @param message - Structured-clone data.
   */
  post(message: unknown): void;
  /**
   * Registers the single message handler of this end; a second call replaces the first handler.
   *
   * @param handler - Called with each message from the other end.
   */
  listen(handler: (message: unknown) => void): void;
  /** Closes this end; nothing is sent or received after it. */
  close(): void;
}

/**
 * An end backed by a `MessagePort` (third-party apps in sandboxed iframes).
 *
 * @param port - The port the desktop transferred with `pithyx:connect`.
 * @returns The transport end; closing it closes the port.
 */
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
 *
 * @returns The two connected ends: one for the app, one for the host. Closing either closes both.
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
