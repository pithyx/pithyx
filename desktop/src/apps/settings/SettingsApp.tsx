import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { IconArrowUpRight } from '../../ui/icons';
import type { SdkAppProps } from '../sdk/CoreSdkApp';
import { internalCall, onInternal, SHARES_CHANGED, STORAGE_CHANGED } from '../sdk/internal';
import styles from './Settings.module.css';
import { sharesKey } from './shares';
import { SharesPage } from './SharesPage';

// Pages in navigation order; later steps add users, network and energy.
const pages = [{ id: 'shares', Component: SharesPage }] as const;

/**
 * Settings: system settings only (users and groups, shared folders, network, energy, updates). Pages ship with the
 * step that makes them work; storage has its own app, which the navigation links to.
 */
export function SettingsApp({ app }: SdkAppProps) {
  const { t } = useTranslation('settings');
  const queryClient = useQueryClient();
  const [current, setCurrent] = useState(0);
  const Page = (pages[current] ?? pages[0]).Component;

  // The desktop forwards changes of the shares and the pools (used space); this syncs every cached answer with them.
  useEffect(() => {
    const refresh = () => {
      void queryClient.invalidateQueries({ queryKey: sharesKey });
    };
    const stops = [
      onInternal(app, SHARES_CHANGED, refresh),
      onInternal(app, STORAGE_CHANGED, refresh),
    ];
    return () => {
      stops.forEach((stop) => {
        stop();
      });
    };
  }, [app, queryClient]);

  return (
    <div className={styles.app}>
      <nav className={styles.nav} aria-label={t('sections')}>
        {pages.map((entry, index) => (
          <button
            key={entry.id}
            type="button"
            className={styles.navItem}
            aria-current={index === current ? 'page' : undefined}
            onClick={() => {
              setCurrent(index);
            }}
          >
            {t(`nav.${entry.id}`)}
          </button>
        ))}
        <button
          type="button"
          className={styles.navLink}
          onClick={() => {
            void internalCall(app, 'internal.admin.openApp', { app: 'storage' });
          }}
        >
          {t('storagePointer.open')}
          <IconArrowUpRight size={14} aria-hidden />
        </button>
      </nav>
      {/* Focusable, so keyboard users can scroll a page without controls. */}
      <main className={styles.page} tabIndex={0}>
        <Page app={app} />
      </main>
    </div>
  );
}
