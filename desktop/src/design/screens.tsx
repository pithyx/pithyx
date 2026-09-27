import type { ReactNode } from 'react';
import { Button } from '../ui/Button';
import { LoginFlow, SetupAccount, SetupRecovery, type LoginStep } from './AuthScreens';
import {
  DesktopScene,
  ErrorDialogScene,
  NotificationsScene,
  StartMenuScene,
  SwitcherScene,
  WindowStatesScene,
  type SceneContext,
} from './DesktopScreens';
import { labels } from './labels';
import { LogoScreen } from './LogoScreen';
import styles from './screens.module.css';
import { TokensScreen } from './TokensScreen';

export type ScreenGroup = keyof typeof labels.groups;

export interface ScreenDefinition {
  id: string;
  group: ScreenGroup;
  title: string;
  description: string;
  render: (context: SceneContext) => ReactNode;
}

function replayControl(replay: () => void) {
  return (
    <Button size="sm" className={styles.replay} onPress={replay}>
      {labels.replay}
    </Button>
  );
}

function login(initial: LoginStep) {
  return (context: SceneContext) => (
    <LoginFlow
      initial={initial}
      desktop={(entering) => <DesktopScene context={context} entering={entering} />}
      controls={replayControl}
    />
  );
}

// Descriptions are for the reviewer and say what to look at.
export const screens: ScreenDefinition[] = [
  {
    id: 'tokens',
    group: 'foundations',
    title: 'Tokens and components',
    description:
      'Semantic colors with measured contrast, accent presets, type scale, spacing, radii, elevation, motion and the React Aria primitives.',
    render: () => <TokensScreen />,
  },
  {
    id: 'logo',
    group: 'foundations',
    title: 'Logo',
    description:
      'All variants of the mark: static, closed, hover (hover or focus the box), welcome (replay), loop and loading, the small sizes and the word mark with a real theta.',
    render: () => <LogoScreen />,
  },
  {
    id: 'setup',
    group: 'auth',
    title: 'Setup: administrator',
    description:
      'First run. Card on the desktop wallpaper, step indicator, password with reveal button.',
    render: () => <SetupAccount />,
  },
  {
    id: 'setup-recovery',
    group: 'auth',
    title: 'Setup: recovery key',
    description: 'Shown once. Continue is enabled only after confirming the key was stored.',
    render: () => <SetupRecovery />,
  },
  {
    id: 'login',
    group: 'auth',
    title: 'Sign in',
    description:
      'Clickable: enter any password to reach the TOTP step, any six digits for the welcome state and the desktop entrance. "Forgot password?" opens the reset step. Replay restarts.',
    render: login('password'),
  },
  {
    id: 'login-forgot',
    group: 'auth',
    title: 'Sign in: forgot password',
    description:
      'No reset by email. Users ask an administrator; administrators reset with the recovery key (POST /api/auth/recover).',
    render: login('forgot'),
  },
  {
    id: 'login-totp',
    group: 'auth',
    title: 'Sign in: TOTP step',
    description: 'Second step inside the same card, with the user shown and a way back.',
    render: login('totp'),
  },
  {
    id: 'login-welcome',
    group: 'auth',
    title: 'Welcome transition',
    description:
      'Lasts only as long as loading (simulated 1.4 s). The loading hint appears after 300 ms. The card fades over the same wallpaper, the taskbar slides in. Reduced motion keeps only fades.',
    render: login('welcome'),
  },
  {
    id: 'desktop-empty',
    group: 'desktop',
    title: 'Empty desktop',
    description: 'Wallpaper and taskbar: start, status area with clock and user. No tray.',
    render: (context) => <DesktopScene context={context} items={false} />,
  },
  {
    id: 'desktop-items',
    group: 'desktop',
    title: 'Desktop with items',
    description:
      'Folders, files and app shortcuts from the Desktop folder, one selected, and a sticky note.',
    render: (context) => <DesktopScene context={context} />,
  },
  {
    id: 'desktop-windows',
    group: 'desktop',
    title: 'Three windows',
    description:
      'Expanded: free windows, one focused. Tablet: split view of the first two. Phone: the focused app full screen.',
    render: (context) => <DesktopScene context={context} windows="three" />,
  },
  {
    id: 'desktop-maximized',
    group: 'desktop',
    title: 'Maximized window',
    description:
      'Settings maximized, showing the personalization page (theme, accent presets, taskbar position, wallpaper). Files is minimized.',
    render: (context) => <DesktopScene context={context} windows="maximized" items={false} />,
  },
  {
    id: 'window-states',
    group: 'desktop',
    title: 'Window states',
    description:
      'Focused, unfocused and maximized frames, and the taskbar states for focused, open and minimized.',
    render: (context) => (
      <WindowStatesScene
        context={context}
        heading="Window states"
        specimens={[
          { name: 'focused', caption: 'Focused: stronger shadow and border, full-color icon.' },
          { name: 'unfocused', caption: 'Unfocused: flatter, muted title and icon.' },
          {
            name: 'maximized',
            caption:
              'Maximized: no radius or border, restore button. Files shown with compact density.',
          },
          {
            name: 'taskbar',
            caption: 'Taskbar: focused (accent bar), open (dot), minimized (hollow dot, dimmed).',
          },
        ]}
      />
    ),
  },
  {
    id: 'app-switcher',
    group: 'desktop',
    title: 'App switcher (phone)',
    description:
      'The compact size class switches apps through cards. Opened from the grid button in the bottom bar.',
    render: (context) => <SwitcherScene context={context} />,
  },
  {
    id: 'start-menu',
    group: 'overlays',
    title: 'Start menu',
    description: 'Search first, pinned apps, recent files, user and power options.',
    render: (context) => <StartMenuScene context={context} search={false} />,
  },
  {
    id: 'start-search',
    group: 'overlays',
    title: 'Start menu search',
    description:
      'Results grouped into apps, settings and files; the first result opens with Enter.',
    render: (context) => <StartMenuScene context={context} search />,
  },
  {
    id: 'user-menu',
    group: 'overlays',
    title: 'User menu',
    description: 'Opened from the profile at the bottom of the start menu.',
    render: (context) => <StartMenuScene context={context} search={false} userMenuOpen />,
  },
  {
    id: 'notifications',
    group: 'overlays',
    title: 'Notification toasts',
    description: 'Toasts next to the status area, with tone and an optional action.',
    render: (context) => <NotificationsScene context={context} panel={false} />,
  },
  {
    id: 'notification-panel',
    group: 'overlays',
    title: 'Notifications and jobs',
    description: 'Panel behind the bell: running jobs with progress first, then notifications.',
    render: (context) => <NotificationsScene context={context} panel />,
  },
  {
    id: 'error-dialog',
    group: 'overlays',
    title: 'Error dialog',
    description:
      'Translated error text, request id with copy button. Becomes a bottom sheet on phones.',
    render: (context) => <ErrorDialogScene context={context} />,
  },
];
