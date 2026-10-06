import { IconServer2 } from '../ui/icons';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { cx } from '@pithyx/ui';
import { Wordmark, type LogoMotion } from '../ui/Logo';
import styles from './AuthLayout.module.css';
import { Wallpaper } from './Wallpaper';

/** Setup and sign-in: one card on the desktop wallpaper, so the desktop appears behind it without a jump. */
export function AuthLayout({
  children,
  footer,
  wide = false,
  entrance = true,
  leaving = false,
  onLeft,
}: {
  children: ReactNode;
  footer?: ReactNode;
  wide?: boolean;
  /** The card slides in; off for the welcome card, which takes the place of the sign-in card. */
  entrance?: boolean;
  /** Fades out; the desktop behind is usable at once, so the fade never adds waiting time. */
  leaving?: boolean;
  onLeft?: () => void;
}) {
  return (
    <div
      className={cx(styles.auth, leaving && styles.leaving)}
      data-leaving={leaving || undefined}
      aria-hidden={leaving || undefined}
      onAnimationEnd={(event) => {
        if (leaving && event.target === event.currentTarget) {
          onLeft?.();
        }
      }}
    >
      <Wallpaper />
      <div className={styles.center}>
        <main className={cx(styles.card, wide && styles.wide, !entrance && styles.still)}>
          {children}
        </main>
        {footer !== undefined && <footer className={styles.footer}>{footer}</footer>}
      </div>
    </div>
  );
}

/** Word mark and the server name, so users with several boxes know where they sign in. */
export function AuthHeader({ host, motion }: { host: string; motion: LogoMotion }) {
  const { t } = useTranslation('auth');
  const { t: tDesktop } = useTranslation('desktop');
  return (
    <div className={styles.header}>
      <Wordmark name={tDesktop('brand')} mark={tDesktop('brandMark')} motion={motion} />
      <p className={styles.host}>
        <IconServer2 size={14} stroke={1.75} aria-hidden />
        <span className="visually-hidden">{t('login.host', { host })}</span>
        <span aria-hidden>{host}</span>
      </p>
    </div>
  );
}
