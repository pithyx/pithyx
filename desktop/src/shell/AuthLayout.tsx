import { IconServer2 } from '@tabler/icons-react';
import type { ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { cx } from '../ui/cx';
import { Wordmark } from '../ui/Logo';
import styles from './AuthLayout.module.css';
import { Wallpaper } from './Wallpaper';

/** Setup and sign-in: one card on the desktop wallpaper, so the desktop appears behind it without a jump. */
export function AuthLayout({
  children,
  footer,
  wide = false,
  leaving = false,
  onLeft,
}: {
  children: ReactNode;
  footer?: ReactNode;
  wide?: boolean;
  leaving?: boolean;
  onLeft?: () => void;
}) {
  return (
    <div
      className={cx(styles.auth, leaving && styles.leaving)}
      onAnimationEnd={(event) => {
        if (leaving && event.target === event.currentTarget) {
          onLeft?.();
        }
      }}
    >
      <Wallpaper />
      <div className={styles.center}>
        <main className={cx(styles.card, wide && styles.wide)}>{children}</main>
        {footer !== undefined && <footer className={styles.footer}>{footer}</footer>}
      </div>
    </div>
  );
}

/** Word mark and the server name, so users with several boxes know where they sign in. */
export function AuthHeader({ host, product }: { host: string; product: string }) {
  const { t } = useTranslation('auth');
  return (
    <div className={styles.header}>
      <Wordmark name={product} />
      <p className={styles.host}>
        <IconServer2 size={14} stroke={1.75} aria-hidden />
        <span className="visually-hidden">{t('login.host', { host })}</span>
        <span aria-hidden>{host}</span>
      </p>
    </div>
  );
}
