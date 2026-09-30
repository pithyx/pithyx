import { PithyxApiError } from '@pithyx/api-client';
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
  return error instanceof PithyxError
    ? error
    : new PithyxError({ code: 'system.internal.unexpected', params: {} });
}
