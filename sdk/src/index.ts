/**
 * `@pithyx/sdk`: the one API every Pithyx app uses, core apps inside the desktop and third-party apps in sandboxed
 * windows alike.
 *
 * A third-party app calls {@link connect} once at start:
 *
 * ```ts
 * import { connect } from '@pithyx/sdk';
 *
 * const app = await connect();
 * await app.window.setTitle('Notes');
 * app.on('themeChange', () => render());
 * app.answer('beforeClose', () => ({ allow: !hasUnsavedChanges() }));
 * const messages = await (await app.backend.fetch('/messages')).json();
 * ```
 *
 * Core apps use the same API over the in-process transport ({@link createApp} with {@link inProcessPair}); they
 * never reach the daemon or the desktop past it.
 *
 * @packageDocumentation
 */
import { createApp, type CreateAppOptions, type PithyxApp } from './app';
import { preventBrowserMenu } from './contextMenu';
import { sdkError } from './errors';
import type { ConnectMessage } from './protocol';
import { portEndpoint } from './transport';

export {
  createApp,
  type CreateAppOptions,
  type EventHandler,
  type PithyxApp,
  type QueryHandler,
} from './app';
export { isProblem, sdkErrorCodes, PithyxError, type Problem, type SdkErrorCode } from './errors';
export { createFormatters, type Formatters } from './format';
export type * from './files';
export type * from './manifest';
export {
  BEFORE_CLOSE_TIMEOUT,
  BEFORE_UNLOAD_TIMEOUT,
  REQUEST_TIMEOUT,
  UNTIMED_METHODS,
  type AppContext,
  type AppLogLevel,
  type AppMessage,
  type BackendInfo,
  type BackendStatus,
  type BackendTicket,
  type BackendUnavailableReason,
  type BackendToken,
  type ConnectMessage,
  type ContextMenuItem,
  type DirectoryGroup,
  type DirectoryUser,
  type EventName,
  type Events,
  type FileRef,
  type GroupRef,
  type HostMessage,
  type LaunchArgs,
  type LogData,
  type LogLevel,
  type LogRecord,
  type MethodName,
  type Methods,
  type NotificationAction,
  type NotificationActivation,
  type NotificationRequest,
  type Point,
  type Queries,
  type QueryName,
  type SizeClass,
  type StorageInfo,
  type StorageState,
  type UnloadReason,
} from './protocol';
export { applyTheme, themeTokenNames, type Theme, type ThemeTokenName } from './theme';
export { inProcessPair, portEndpoint, type Endpoint } from './transport';
export { MIN_PROTOCOL_VERSION, PROTOCOL_VERSION, SDK_VERSION } from './version';

/** How long {@link connect} waits for the desktop's port before it gives up. */
const CONNECT_TIMEOUT = 10_000;

function isConnectMessage(value: unknown): value is ConnectMessage {
  return (
    typeof value === 'object' &&
    value !== null &&
    (value as { type?: unknown }).type === 'pithyx:connect'
  );
}

/**
 * Connects a sandboxed app to the desktop. It accepts exactly one `pithyx:connect` message, only from the parent
 * window (the desktop that framed the app) and only with exactly one transferred port; every other window message is
 * ignored, before and after. Rejects with `sdk.transport.closed` when no desktop connects in time, for example when
 * the page was opened outside the desktop. From then on a right-click in the app never opens the browser's own menu,
 * except in text fields; an app shows its own menu by listening for `contextmenu`.
 *
 * @param options - Theme root, call time limit and box address as for {@link createApp}, plus how long to wait for
 * the desktop.
 * @returns The app API, once the desktop accepted the app.
 * @throws `sdk.transport.closed` when no desktop connects within the time limit.
 * @throws `sdk.protocol.unsupported` when the desktop does not speak the SDK's protocol version.
 */
export function connect(
  options: CreateAppOptions & {
    /** How long to wait for the desktop's port in milliseconds; 10 seconds when left out. */
    connectTimeout?: number;
  } = {},
): Promise<PithyxApp> {
  preventBrowserMenu(document);
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      window.removeEventListener('message', onMessage);
      reject(sdkError('sdk.transport.closed', {}, 'No desktop connected'));
    }, options.connectTimeout ?? CONNECT_TIMEOUT);

    function onMessage(event: MessageEvent) {
      const port = event.ports[0];
      if (
        event.source !== window.parent ||
        window.parent === window ||
        !isConnectMessage(event.data)
      ) {
        return;
      }
      if (event.ports.length !== 1 || port === undefined) {
        return;
      }
      window.removeEventListener('message', onMessage);
      clearTimeout(timer);
      createApp(portEndpoint(port), options).then(resolve, reject);
    }

    window.addEventListener('message', onMessage);
  });
}
