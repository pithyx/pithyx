import { useQuery } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { fetchHealth } from './api/health';

export function DaemonStatus() {
  const { t } = useTranslation();
  const health = useQuery({
    queryKey: ['health'],
    queryFn: ({ signal }) => fetchHealth(signal),
  });

  let text: string;
  if (health.isSuccess) {
    text = t('status.connected', { mode: health.data.mode });
  } else if (health.isError) {
    text = t('status.unreachable');
  } else {
    text = t('status.connecting');
  }

  return (
    <p className="daemon-status" role="status" data-state={health.status}>
      {text}
    </p>
  );
}
