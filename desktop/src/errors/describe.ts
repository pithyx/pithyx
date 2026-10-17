import { PithyxApiError, type ErrorCode } from '@pithyx/api-client';

/** What the UI needs to show an error: a translatable code and its parameters; the request id is for the console. */
export interface DescribedError {
  code: ErrorCode | 'network';
  params: Record<string, unknown>;
  requestId: string;
  /** HTTP status of a daemon answer, if known. */
  status?: number;
  /** The problem's English text, shown only for a code the desktop has no translation for (an app's own code). */
  detail?: string;
}

export function describeError(error: unknown): DescribedError {
  if (error instanceof PithyxApiError) {
    return {
      code: error.code,
      params: error.params,
      requestId: error.requestId,
      status: error.status,
      detail: error.message,
    };
  }
  // fetch rejects with a TypeError when the daemon cannot be reached at all.
  return { code: 'network', params: {}, requestId: '' };
}

export function isSessionExpired(error: unknown): boolean {
  return error instanceof PithyxApiError && error.code === 'auth.session.expired';
}
