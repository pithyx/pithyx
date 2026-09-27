/**
 * `@vyxis/sdk`: the one API every Vyxis app uses, core apps inside the desktop and third-party apps in sandboxed
 * windows alike.
 *
 * A third-party app calls {@link connect} once at start:
 *
 * ```ts
 * import { connect } from '@vyxis/sdk';
 *
 * const app = await connect();
 * await app.window.setTitle('Notes');
 * app.on('themeChange', () => render());
 * app.answer('beforeClose', () => ({ allow: !hasUnsavedChanges() }));
 * ```
 *
 * @packageDocumentation
 */
import { createApp, type CreateAppOptions, type VyxisApp } from './app';
import { sdkError } from './errors';
import type { ConnectMessage } from './protocol';
import { portEndpoint } from './transport';

export { createApp, type CreateAppOptions, type VyxisApp } from './app';
export { isProblem, sdkErrorCodes, VyxisError, type Problem, type SdkErrorCode } from './errors';
export type * from './manifest';
export {
  BEFORE_CLOSE_TIMEOUT,
  BEFORE_LOGOUT_TIMEOUT,
  REQUEST_TIMEOUT,
  type AppContext,
  type AppMessage,
  type ConnectMessage,
  type EventName,
  type Events,
  type HostMessage,
  type LaunchArgs,
  type MethodName,
  type Methods,
  type NotificationRequest,
  type Queries,
  type QueryName,
  type SizeClass,
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
    (value as { type?: unknown }).type === 'vyxis:connect'
  );
}

/**
 * Connects a sandboxed app to the desktop. It accepts exactly one `vyxis:connect` message, only from the parent
 * window (the desktop that framed the app) and only with exactly one transferred port; every other window message is
 * ignored, before and after. Rejects with `sdk.transport.closed` when no desktop connects in time, for example when
 * the page was opened outside the desktop.
 */
export function connect(
  options: CreateAppOptions & { connectTimeout?: number } = {},
): Promise<VyxisApp> {
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
