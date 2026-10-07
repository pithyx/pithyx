/**
 * Every failed SDK call rejects with a {@link PithyxError}. Errors from the daemon keep their RFC 9457 problem details
 * unchanged (`code`, `params`, `detail`, `requestId`), so an app shows the same stable code the API returned.
 * Errors that happen between the app and the desktop use the `sdk.*` codes below. Permission and limit errors are
 * the daemon's (`app.permission.denied`, `app.storage.too_large`), because the daemon is where they are checked.
 *
 * Apps translate codes themselves (they get the user's locale in the init context); `detail` is an English fallback.
 */

/** Codes the SDK itself can produce. Codes of the daemon API come through unchanged and are strings as well. */
export const sdkErrorCodes = [
  /** The desktop does not know the method (an app built against a newer SDK than the desktop). */
  'sdk.method.unknown',
  /** The parameters of a call do not match the method. */
  'sdk.request.invalid',
  /** The desktop did not answer within the time limit. */
  'sdk.request.timeout',
  /** The connection to the desktop is closed (window closed, app reloaded, desktop signed out). */
  'sdk.transport.closed',
  /** The desktop does not speak the app's protocol version. */
  'sdk.protocol.unsupported',
  /** The user cancelled the upload in the taskbar. */
  'sdk.transfer.cancelled',
  /** The app has no backend, or its backend declares no endpoint of this name (`params.endpoint`). */
  'sdk.backend.endpoint_unknown',
  /** A request to the app's backend did not reach the box (`params.endpoint`), for example while it restarts. */
  'sdk.backend.unreachable',
] as const;

/** A code of the SDK's own transport errors, translated under `errors.sdk.*`. */
export type SdkErrorCode = (typeof sdkErrorCodes)[number];

/** A problem as it travels between desktop and app: RFC 9457 fields the Pithyx API uses. */
export interface Problem {
  /** Stable code, `area.object.reason`. */
  code: string;
  /** Values for the translated message. */
  params: Record<string, unknown>;
  /** English fallback text. */
  detail?: string;
  /** The request id for support, when the daemon was involved. */
  requestId?: string;
}

export class PithyxError extends Error implements Problem {
  readonly code: string;
  readonly params: Record<string, unknown>;
  readonly detail?: string;
  readonly requestId?: string;

  constructor(problem: Problem) {
    super(problem.detail ?? problem.code);
    this.name = 'PithyxError';
    this.code = problem.code;
    this.params = problem.params;
    if (problem.detail !== undefined) {
      this.detail = problem.detail;
    }
    if (problem.requestId !== undefined) {
      this.requestId = problem.requestId;
    }
  }

  /** The plain problem, for sending it over a transport. */
  toProblem(): Problem {
    return {
      code: this.code,
      params: this.params,
      ...(this.detail === undefined ? {} : { detail: this.detail }),
      ...(this.requestId === undefined ? {} : { requestId: this.requestId }),
    };
  }
}

/** A {@link PithyxError} with one of the SDK's own transport codes. */
export function sdkError(
  code: SdkErrorCode,
  params: Record<string, unknown> = {},
  detail?: string,
): PithyxError {
  return new PithyxError({ code, params, ...(detail === undefined ? {} : { detail }) });
}

/** Turns anything a handler threw into a problem that can cross the transport. */
export function toProblem(error: unknown): Problem {
  if (error instanceof PithyxError) {
    return error.toProblem();
  }
  if (isProblem(error)) {
    return error;
  }
  return {
    code: 'system.internal.unexpected',
    params: {},
    detail: 'Unexpected error in the desktop',
  };
}

/** Whether a value has the shape of a problem (RFC 9457 fields `code` and `params`). */
export function isProblem(value: unknown): value is Problem {
  return (
    typeof value === 'object' &&
    value !== null &&
    typeof (value as { code?: unknown }).code === 'string' &&
    typeof (value as { params?: unknown }).params === 'object'
  );
}
