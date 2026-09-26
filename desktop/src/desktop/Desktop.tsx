import { useMutation, useQueryClient } from '@tanstack/react-query';
import { unwrap, type components } from '@vyxis/api-client';
import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { api } from '../api/client';
import { queryKeys } from '../api/queries';
import { TotpSetup } from '../auth/TotpSetup';
import { DaemonStatus } from '../DaemonStatus';
import { showError } from '../errors/errorStore';
import { realtime, useRealtime } from '../realtime/connection';
import { DisksWindow } from './DisksWindow';
import { LanguageSelect } from './LanguageSelect';

type User = components['schemas']['UserInfo'];

/** The walking-skeleton desktop: a top bar and one fixed window. The window manager comes in step 5. */
export function Desktop({ user }: { user: User }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const status = useRealtime((state) => state.status);
  const [totpOpen, setTotpOpen] = useState(false);

  // The WebSocket lives as long as the desktop; a closed session sends the user back to the login screen.
  useEffect(() => {
    realtime.connect(() => {
      void queryClient.invalidateQueries({ queryKey: queryKeys.session });
    });
    return () => {
      realtime.disconnect();
    };
  }, [queryClient]);

  const logout = useMutation({
    mutationFn: async () => {
      unwrap(await api.POST('/api/auth/logout'));
    },
    onSuccess: async () => {
      await queryClient.resetQueries({ queryKey: queryKeys.session });
    },
    onError: showError,
  });

  return (
    <div className="desktop">
      <header className="top-bar">
        <span className="top-bar__brand">{t('brand')}</span>
        <DaemonStatus />
        <span className="top-bar__spacer" />
        <span className="top-bar__user">
          {t('menu.signedInAs', { name: user.displayName ?? user.username })}
        </span>
        <LanguageSelect />
        <button
          type="button"
          className="button button--small"
          onClick={() => {
            setTotpOpen(true);
          }}
        >
          {t('menu.twoFactor')}
        </button>
        <button
          type="button"
          className="button button--small"
          onClick={() => {
            logout.mutate();
          }}
          disabled={logout.isPending}
        >
          {t('menu.signOut')}
        </button>
      </header>
      {status === 'reconnecting' && (
        <p className="banner" role="status">
          {t('realtime.reconnecting')}
        </p>
      )}
      {status === 'stopped' && (
        <p className="banner banner--danger" role="alert">
          {t('realtime.stopped')}
        </p>
      )}
      <main className="desktop-surface">
        <DisksWindow />
      </main>
      {totpOpen && (
        <TotpSetup
          user={user}
          onClose={() => {
            setTotpOpen(false);
          }}
        />
      )}
    </div>
  );
}
