import { IconDatabase, IconLanguage, IconLogout, IconShieldLock } from '@tabler/icons-react';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { unwrap, type components } from '@vyxis/api-client';
import { useEffect, useState, type ReactNode } from 'react';
import { Header, MenuSection, SubmenuTrigger } from 'react-aria-components';
import { useTranslation } from 'react-i18next';
import { api } from '../api/client';
import { queryKeys } from '../api/queries';
import { initialsOf, LanguageOptions } from '../auth/parts';
import { TotpSetup } from '../auth/TotpSetup';
import { useDaemonStatus } from '../daemonStatus';
import { showError } from '../errors/errorStore';
import { realtime, useRealtime } from '../realtime/connection';
import { DesktopShell } from '../shell/DesktopShell';
import { Taskbar } from '../shell/Taskbar';
import type { AppInfo } from '../shell/types';
import { Window } from '../shell/Window';
import { cx } from '../ui/cx';
import { Menu, MenuItem, MenuPopover, MenuSeparator, MenuTrigger } from '../ui/Menu';
import { useMinuteClock } from './clock';
import styles from './Desktop.module.css';
import { DisksContent } from './DisksContent';

type User = components['schemas']['UserInfo'];

function UserMenu({
  trigger,
  name,
  onTwoFactor,
  onSignOut,
}: {
  trigger: ReactNode;
  name: string;
  onTwoFactor: () => void;
  onSignOut: () => void;
}) {
  const { t } = useTranslation();
  const status = useDaemonStatus();
  return (
    <MenuTrigger>
      {trigger}
      <MenuPopover placement="top end">
        <Menu
          aria-label={t('taskbar.userMenu', { name })}
          onAction={(key) => {
            if (key === 'two-factor') {
              onTwoFactor();
            } else if (key === 'sign-out') {
              onSignOut();
            }
          }}
        >
          <MenuSection>
            <Header className={styles.menuHeader}>
              <span className={styles.menuName}>{t('menu.signedInAs', { name })}</span>
              <span>{status}</span>
            </Header>
            <MenuItem id="two-factor" icon={<IconShieldLock size={18} stroke={1.75} />}>
              {t('menu.twoFactor')}
            </MenuItem>
            <SubmenuTrigger>
              <MenuItem id="language" icon={<IconLanguage size={18} stroke={1.75} />}>
                {t('menu.language')}
              </MenuItem>
              <MenuPopover>
                <LanguageOptions />
              </MenuPopover>
            </SubmenuTrigger>
          </MenuSection>
          <MenuSeparator />
          <MenuItem id="sign-out" icon={<IconLogout size={18} stroke={1.75} />}>
            {t('menu.signOut')}
          </MenuItem>
        </Menu>
      </MenuPopover>
    </MenuTrigger>
  );
}

/**
 * The desktop after sign-in: wallpaper, taskbar and the live disk list in a window. The window manager replaces the
 * fixed window in step 5.
 */
export function Desktop({ user, entering }: { user: User; entering: boolean }) {
  const { t, i18n } = useTranslation();
  const queryClient = useQueryClient();
  const status = useRealtime((state) => state.status);
  const now = useMinuteClock();
  const [totpOpen, setTotpOpen] = useState(false);
  const name = user.displayName ?? user.username;

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

  const disks: AppInfo = {
    id: 'disks',
    name: t('window.title', { ns: 'disks' }),
    icon: IconDatabase,
    tone: 'ocean',
  };

  return (
    <DesktopShell
      entering={entering}
      taskbar={
        <Taskbar
          position="bottom"
          apps={[{ ...disks, state: 'focused' }]}
          jobs={0}
          unread={0}
          now={now}
          locale={i18n.language}
          user={{ name, initials: initialsOf(name) }}
          userMenu={(trigger) => (
            <UserMenu
              trigger={trigger}
              name={name}
              onTwoFactor={() => {
                setTotpOpen(true);
              }}
              onSignOut={() => {
                logout.mutate();
              }}
            />
          )}
        />
      }
      overlays={
        <>
          {status === 'reconnecting' && (
            <p className={styles.banner} role="status">
              {t('realtime.reconnecting')}
            </p>
          )}
          {status === 'stopped' && (
            <p className={cx(styles.banner, styles.danger)} role="alert">
              {t('realtime.stopped')}
            </p>
          )}
          <TotpSetup
            user={user}
            isOpen={totpOpen}
            onClose={() => {
              setTotpOpen(false);
            }}
          />
        </>
      }
    >
      <Window
        app={disks}
        title={disks.name}
        focused
        geometry={{ x: 6, y: 6, width: 72, height: 80 }}
        compactActive
        layer={1}
      >
        <DisksContent />
      </Window>
    </DesktopShell>
  );
}
