import { unwrap } from '@vyxis/api-client';
import { api } from './client';

export const queryKeys = {
  setup: ['setup'],
  session: ['session'],
  health: ['health'],
} as const;

export async function fetchSetupStatus(signal: AbortSignal) {
  return unwrap(await api.GET('/api/setup', { signal }));
}

export async function fetchSession(signal: AbortSignal) {
  return unwrap(await api.GET('/api/auth/session', { signal }));
}
