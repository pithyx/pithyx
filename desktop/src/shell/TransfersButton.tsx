import type { TFunction } from 'i18next';
import {
  Button as AriaButton,
  Dialog,
  DialogTrigger,
  Heading,
  Popover,
} from 'react-aria-components';
import { useTranslation } from 'react-i18next';
import { formatBytes } from '../desktop/format';
import { cx } from '../ui/cx';
import { IconDownload, IconUpload, IconX } from '../ui/icons';
import menuStyles from '../ui/Menu.module.css';
import taskbarStyles from './Taskbar.module.css';
import {
  cancelTransfer,
  dismissTransfer,
  secondsLeft,
  useTransfers,
  type Transfer,
} from './transfers';
import styles from './TransfersButton.module.css';
import { useFormatLocale } from '../i18n';

/**
 * Uploads and downloads of every app while they run: a button with the overall progress as a ring, and a list with a
 * progress bar, the sizes, the speed, the time left and a cancel button per transfer. Hidden when nothing runs.
 */
export function TransfersButton({ position }: { position: 'bottom' | 'top' | 'left' | 'right' }) {
  const { t } = useTranslation();
  const transfers = useTransfers((state) => state.transfers);
  if (transfers.length === 0) {
    return null;
  }
  const total = transfers.reduce((sum, transfer) => sum + transfer.total, 0);
  const loaded = transfers.reduce((sum, transfer) => sum + transfer.loaded, 0);
  const share = total === 0 ? 0 : Math.min(1, loaded / total);
  const failed = transfers.some((transfer) => transfer.failed);
  const placement = { bottom: 'top', top: 'bottom', left: 'right', right: 'left' } as const;

  return (
    <DialogTrigger>
      <AriaButton
        className={cx(taskbarStyles.button, styles.button)}
        aria-label={t('transfers.button', {
          count: transfers.length,
          percent: Math.round(share * 100),
        })}
        data-failed={failed || undefined}
      >
        <svg className={styles.ring} viewBox="0 0 36 36" aria-hidden>
          <circle className={styles.track} cx="18" cy="18" r="15" />
          <circle
            className={styles.value}
            cx="18"
            cy="18"
            r="15"
            pathLength={100}
            strokeDasharray={`${String(share * 100)} 100`}
          />
        </svg>
        {transfers.every((transfer) => transfer.kind === 'upload') ? (
          <IconUpload className={styles.icon} size={16} stroke={2} />
        ) : (
          <IconDownload className={styles.icon} size={16} stroke={2} />
        )}
      </AriaButton>
      <Popover className={cx(menuStyles.popover, styles.popover)} placement={placement[position]}>
        <Dialog className={styles.dialog} aria-labelledby="transfers-title">
          <Heading slot="title" id="transfers-title" className={styles.title}>
            {t('transfers.title')}
          </Heading>
          <ul className={styles.list}>
            {transfers.map((transfer) => (
              <TransferRow key={transfer.id} transfer={transfer} />
            ))}
          </ul>
        </Dialog>
      </Popover>
    </DialogTrigger>
  );
}

function TransferRow({ transfer }: { transfer: Transfer }) {
  const { t } = useTranslation();
  const locale = useFormatLocale();
  const percent =
    transfer.total === 0 ? null : Math.round((transfer.loaded / transfer.total) * 100);
  const left = secondsLeft(transfer);
  const details = transfer.failed
    ? t('transfers.failed')
    : [
        transfer.total === 0
          ? formatBytes(transfer.loaded, locale)
          : t('transfers.size', {
              loaded: formatBytes(transfer.loaded, locale),
              total: formatBytes(transfer.total, locale),
            }),
        transfer.speed > 0
          ? t('transfers.speed', { speed: formatBytes(transfer.speed, locale) })
          : null,
        left === null ? null : timeLeft(t, left),
      ]
        .filter((part) => part !== null)
        .join(' · ');
  const label = t(transfer.kind === 'upload' ? 'transfers.upload' : 'transfers.download', {
    name: transfer.name,
  });

  return (
    <li className={styles.row} data-failed={transfer.failed || undefined}>
      <span className={styles.kind} aria-hidden>
        {transfer.kind === 'upload' ? (
          <IconUpload size={16} stroke={1.75} />
        ) : (
          <IconDownload size={16} stroke={1.75} />
        )}
      </span>
      <span className={styles.name} title={transfer.name}>
        {transfer.name}
      </span>
      <AriaButton
        className={styles.cancel}
        aria-label={
          transfer.failed
            ? t('transfers.dismiss', { name: transfer.name })
            : t('transfers.cancel', { name: transfer.name })
        }
        onPress={() => {
          if (transfer.failed) {
            dismissTransfer(transfer.id);
          } else {
            cancelTransfer(transfer.id);
          }
        }}
      >
        <IconX size={16} stroke={1.75} />
      </AriaButton>
      <div
        className={styles.bar}
        role="progressbar"
        aria-label={label}
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={percent ?? undefined}
        aria-valuetext={details}
      >
        <span style={{ width: `${String(percent ?? 0)}%` }} />
      </div>
      <span className={styles.details}>{details}</span>
    </li>
  );
}

function timeLeft(t: TFunction, seconds: number): string {
  if (seconds < 60) {
    return t('transfers.secondsLeft', { count: seconds });
  }
  if (seconds < 3600) {
    return t('transfers.minutesLeft', { count: Math.round(seconds / 60) });
  }
  return t('transfers.hoursLeft', {
    hours: Math.floor(seconds / 3600),
    minutes: Math.round((seconds % 3600) / 60),
  });
}
