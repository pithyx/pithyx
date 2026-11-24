import { cx } from '@pithyx/ui';
import { useTranslation } from 'react-i18next';
import { LogoMark } from '../../ui/Logo';
import {
  IconArrowRight,
  IconCloudUpload,
  IconDownload,
  IconServer2,
  IconUpload,
} from '../../ui/icons';
import styles from './Backup.module.css';

/** Where a backup goes (D416): `out` from this box to a target, `in` from a server onto this box. */
export type Direction = 'out' | 'in';

/** The direction follows from the kind: versioned backups and mirrors leave the box, a pull (#396) comes onto it. */
export function directionOf(kind: string): Direction {
  return kind === 'pull' ? 'in' : 'out';
}

/** The direction as a pill with its icon and words, never by color alone. */
export function DirectionTag({ direction }: { direction: Direction }) {
  const { t } = useTranslation('backup');
  const Icon = direction === 'out' ? IconUpload : IconDownload;
  return (
    <span className={cx(styles.direction, direction === 'out' ? styles.out : styles.in)}>
      <Icon size={14} stroke={2} aria-hidden />
      {t(`direction.${direction}`)}
    </span>
  );
}

/** A backup's route: what it reads, where that goes and which way. */
export interface RouteParts {
  from: string;
  to: string;
  direction: Direction;
}

/**
 * "source -> destination": from the box (its mark) to a target (a cloud), or from a server (a server) onto the box (its
 * mark).
 */
export function Route({
  from,
  to,
  direction = 'out',
}: {
  from: string;
  to: string;
  direction?: Direction;
}) {
  const { t } = useTranslation('backup');
  return (
    <span className={styles.route}>
      {direction === 'out' ? (
        <LogoMark size={14} />
      ) : (
        <IconServer2 size={14} stroke={1.75} aria-hidden />
      )}
      <span className={styles.routeName}>{from}</span>
      <IconArrowRight size={14} stroke={1.75} aria-hidden className={styles.routeArrow} />
      <span className={styles.visuallyHidden}>{t('route.to')}</span>
      {direction === 'out' ? (
        <IconCloudUpload size={14} stroke={1.75} aria-hidden />
      ) : (
        <LogoMark size={14} />
      )}
      <span className={styles.routeName}>{to}</span>
    </span>
  );
}
