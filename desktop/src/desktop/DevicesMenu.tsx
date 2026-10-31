import { IconDeviceDesktop } from '../ui/icons';
import { useMutation, useQuery } from '@tanstack/react-query';
import { unwrap, type components } from '@pithyx/api-client';
import { useTranslation } from 'react-i18next';
import { Menu, MenuItem } from '@pithyx/ui';
import { api } from '../api/client';
import { showError } from '../errors/errorStore';
import { useFormatLocale } from '../i18n';
import { notify } from '../shell/toasts';
import { applySavedLayout } from '../wm/persistence';
import styles from './Desktop.module.css';

type DeviceLayout = components['schemas']['DeviceLayout'];

function useDeviceLabel(): (device: Pick<DeviceLayout, 'browser' | 'os'>) => string {
  const { t } = useTranslation();
  return (device) =>
    t('menu.device', {
      browser: device.browser ?? t('menu.unknownBrowser'),
      os: device.os ?? t('menu.unknownOs'),
    });
}

function ago(iso: string, locale: string): string {
  const format = new Intl.RelativeTimeFormat(locale, { numeric: 'auto' });
  const minutes = Math.round((new Date(iso).getTime() - Date.now()) / 60_000);
  if (Math.abs(minutes) < 60) {
    return format.format(minutes, 'minute');
  }
  const hours = Math.round(minutes / 60);
  return Math.abs(hours) < 48
    ? format.format(hours, 'hour')
    : format.format(Math.round(hours / 24), 'day');
}

/**
 * "Get desktop from ...": the user's other devices with a saved layout, loaded when the menu opens. Picking one takes
 * over its layout on this device.
 */
export function DevicesMenu({ onPicked }: { onPicked?: () => void }) {
  const { t } = useTranslation();
  const locale = useFormatLocale();
  const label = useDeviceLabel();
  const devices = useQuery({
    queryKey: ['desktop', 'devices'],
    queryFn: async ({ signal }) => unwrap(await api.GET('/api/desktop/devices', { signal })),
    staleTime: 0,
  });
  const copy = useMutation({
    mutationFn: async (device: DeviceLayout) =>
      unwrap(await api.POST('/api/desktop/layout/copy', { body: { deviceId: device.deviceId } })),
    onSuccess: (saved, device) => {
      applySavedLayout(saved);
      notify({ code: 'desktop.copied', params: { device: label(device) } });
    },
    onError: showError,
  });
  return (
    <Menu
      aria-label={t('menu.getDesktop')}
      onAction={(key) => {
        const device = devices.data?.find((entry) => entry.deviceId === key);
        if (device !== undefined) {
          copy.mutate(device);
          onPicked?.();
        }
      }}
    >
      {devices.data?.length === 0 && (
        <MenuItem id="none" isDisabled>
          {t('menu.noDevices')}
        </MenuItem>
      )}
      {devices.data?.map((device) => (
        <MenuItem
          key={device.deviceId}
          id={device.deviceId}
          textValue={label(device)}
          icon={<IconDeviceDesktop size={18} stroke={1.75} />}
        >
          <span className={styles.deviceName}>{label(device)}</span>
          <span className={styles.deviceDetail}>
            {device.screenWidth === null || device.screenHeight === null
              ? t('menu.deviceDetail', {
                  count: device.windowCount,
                  time: ago(device.updatedAt, locale),
                })
              : t('menu.deviceDetailScreen', {
                  // Strings, so the size is not formatted with thousands separators.
                  width: String(device.screenWidth),
                  height: String(device.screenHeight),
                  count: device.windowCount,
                  time: ago(device.updatedAt, locale),
                })}
          </span>
        </MenuItem>
      ))}
    </Menu>
  );
}
