import { PithyxApiError, type ErrorCode } from '@pithyx/api-client';

/** What the UI needs to show an error: a translatable code, its parameters and the request id for support. */
export interface DescribedError {
  code: ErrorCode | 'network';
  params: Record<string, unknown>;
  requestId: string;
  /** HTTP status of a daemon answer, if known. */
  status?: number;
}

export function describeError(error: unknown): DescribedError {
  if (error instanceof PithyxApiError) {
    return {
      code: error.code,
      params: error.params,
      requestId: error.requestId,
      status: error.status,
    };
  }
  // fetch rejects with a TypeError when the daemon cannot be reached at all.
  return { code: 'network', params: {}, requestId: '' };
}

/**
 * Whether support needs the request id: the daemon failed (5xx), rather than the user doing something it refuses (a
 * wrong password, an invalid name). Errors without a status count as faults only for system codes.
 */
export function isFault(error: DescribedError): boolean {
  return error.status === undefined
    ? error.code.startsWith('system.') || error.code === 'storage.command.failed'
    : error.status >= 500;
}

export function isSessionExpired(error: unknown): boolean {
  return error instanceof PithyxApiError && error.code === 'auth.session.expired';
}
