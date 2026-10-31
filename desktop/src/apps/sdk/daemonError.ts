import { BoxUnreachableError, PithyxApiError } from '@pithyx/api-client';
import { PithyxError } from '@pithyx/sdk/host';

/** A daemon error as the SDK hands it to an app, with the same code, params and request id. */
export function toPithyxError(error: unknown): PithyxError {
  if (error instanceof PithyxApiError) {
    return new PithyxError({
      code: error.code,
      params: error.params,
      detail: error.message,
      requestId: error.requestId,
    });
  }
  if (error instanceof BoxUnreachableError) {
    return new PithyxError({ code: error.code, params: {}, detail: error.message });
  }
  return error instanceof PithyxError
    ? error
    : new PithyxError({ code: 'system.internal.unexpected', params: {} });
}

/** Runs a daemon call and rejects with the SDK's error for a daemon problem or a box that cannot be reached. */
export async function daemon<T>(call: () => Promise<T>): Promise<T> {
  try {
    return await call();
  } catch (error) {
    throw error instanceof PithyxApiError || error instanceof BoxUnreachableError
      ? toPithyxError(error)
      : error;
  }
}
