import { useQuery } from '@tanstack/react-query';
import { unwrap } from '@vyxis/api-client';
import { useTranslation } from 'react-i18next';
import { api } from './api/client';
import { queryKeys } from './api/queries';

/** "Connected to daemon", with a hint when the daemon answers from recorded fixtures. */
export function useDaemonStatus(): string {
  const { t } = useTranslation();
  const health = useQuery({
    queryKey: queryKeys.health,
    queryFn: async ({ signal }) => unwrap(await api.GET('/api/health', { signal })),
  });
  if (health.isSuccess) {
    return t('status.connected', { mode: health.data.mode });
  }
  return health.isError ? t('status.unreachable') : t('status.connecting');
}
