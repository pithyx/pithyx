import type { TFunction } from 'i18next';

/** How long the box runs, in days, hours and minutes. */
export function uptime(t: TFunction<'settings'>, seconds: number): string {
  const days = Math.floor(seconds / 86_400);
  const hours = Math.floor((seconds % 86_400) / 3600);
  const minutes = Math.floor((seconds % 3600) / 60);
  return t('info.uptimeValue', { days, hours, minutes });
}
