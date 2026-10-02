import { unwrap } from '@pithyx/api-client';
import { applySystemRegion } from '../i18n';
import { api } from './client';

export const queryKeys = {
  setup: ['setup'],
  session: ['session'],
  health: ['health'],
} as const;

/** The setup state; it also carries the system language and region, which apply right away. */
export async function fetchSetupStatus(signal: AbortSignal) {
  const status = unwrap(await api.GET('/api/setup', { signal }));
  await applySystemRegion(status);
  return status;
}

export async function fetchSession(signal: AbortSignal) {
  return unwrap(await api.GET('/api/auth/session', { signal }));
}
