import { queryOptions } from '@tanstack/react-query';
import { deviceTrust } from '../platform/browser';

/** The address any device downloads the box's root from, also before signing in. */
export const rootCertificateUrl = '/api/system/tls/root-ca.crt';

/** Whether this device trusts the box (D380), checked once per page and again on request. */
export const trustQuery = queryOptions({
  queryKey: ['device', 'trust'] as const,
  queryFn: () => deviceTrust(),
  staleTime: Infinity,
  gcTime: Infinity,
});

const HINT_KEY = 'pithyx.trustHint';

/** Whether the user hid the hint on this device; a convenience of this browser only. */
export function trustHintDismissed(): boolean {
  try {
    return localStorage.getItem(HINT_KEY) === 'dismissed';
  } catch {
    return false;
  }
}

export function dismissTrustHint(): void {
  try {
    localStorage.setItem(HINT_KEY, 'dismissed');
  } catch {
    // Without storage the hint comes back on the next visit.
  }
}
