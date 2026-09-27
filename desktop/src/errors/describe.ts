import { PithyxApiError, type ErrorCode } from '@pithyx/api-client';

/** What the UI needs to show an error: a translatable code, its parameters and the request id for support. */
export interface DescribedError {
  code: ErrorCode | 'network';
  params: Record<string, unknown>;
  requestId: string;
}

export function describeError(error: unknown): DescribedError {
  if (error instanceof PithyxApiError) {
    return { code: error.code, params: error.params, requestId: error.requestId };
  }
  // fetch rejects with a TypeError when the daemon cannot be reached at all.
  return { code: 'network', params: {}, requestId: '' };
}

export function isSessionExpired(error: unknown): boolean {
  return error instanceof PithyxApiError && error.code === 'auth.session.expired';
}
