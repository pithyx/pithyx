import type { ReactNode } from 'react';
import { Button } from '@pithyx/ui';
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
import { appScreens } from './appScreens';
import { FileDialogScene } from './FileDialogScene';
import { labels } from './labels';
import { LogoScreen } from './LogoScreen';
import { PatternAppScene, PatternsScreen, RecentFilesScene } from './PatternScreens';
import styles from './screens.module.css';
import { storageScreens } from './storageScreens';
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
    id: 'desktop-window-menu',
    group: 'desktop',
    title: 'Window menu',
    description:
      'The app icon in the title bar of Files opens every window action with its shortcut, without dragging.',
    render: (context) => (
      <DesktopScene
        context={context}
        windows="three"
        windowMenu
        script={[{ press: 'desktop:window.menu', params: { title: '' } }]}
      />
    ),
  },
  {
    id: 'desktop-maximized',
    group: 'desktop',
    title: 'Maximized window',
    description:
      'Settings maximized, showing its overview of tiles by category. Files is minimized.',
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
    description: 'Search first, apps, recent files, user and power options.',
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
    id: 'user-menu-appearance',
    group: 'overlays',
    title: 'User menu: appearance',
    description: 'The submenu Appearance of the user menu: like the system, light or dark.',
    render: (context) => (
      <StartMenuScene
        context={context}
        search={false}
        userMenuOpen
        script={[{ press: 'desktop:menu.theme' }]}
      />
    ),
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
      'Translated error text; the request id goes to the console only. Becomes a bottom sheet on phones.',
    render: (context) => <ErrorDialogScene context={context} />,
  },
  {
    id: 'patterns',
    group: 'patterns',
    title: 'Patterns',
    description:
      'Every pattern of @pithyx/ui from the design pass (D394) on its own: state badges, summary tiles, navigation, attention list, table with details pane, cards with meters, form sections with the action bar. Check every accent.',
    render: (context) => <PatternsScreen context={context} />,
  },
  {
    id: 'pattern-overview',
    group: 'patterns',
    title: 'Sample: settings overview',
    description:
      'AppFrame with the grouped navigation; box facts, Needs attention with one action per row, all settings. Compare with f-overview.',
    render: (context) => <PatternAppScene context={context} page="overview" />,
  },
  {
    id: 'pattern-network',
    group: 'patterns',
    title: 'Sample: network form',
    description:
      'Tabs, interface cards, form sections with label column, radio buttons, a select and the action bar fixed at the bottom. Compare with f-network.',
    render: (context) => <PatternAppScene context={context} page="network" />,
  },
  {
    id: 'pattern-users',
    group: 'patterns',
    title: 'Sample: users with details pane',
    description:
      'Tabs with counts, toolbar with the primary action first, a sortable table with two-line rows and the details pane of the selected user. Compare with f-users.',
    render: (context) => <PatternAppScene context={context} page="users" />,
  },
  {
    id: 'pattern-pools',
    group: 'patterns',
    title: 'Sample: pools as cards',
    description:
      'Summary tiles, toolbar, pool cards with state badge, facts, meter (orange when nearly full) and the expanded details with the disks and actions. Compare with f-storage.',
    render: (context) => <PatternAppScene context={context} page="pools" />,
  },
  {
    id: 'files-recent',
    group: 'patterns',
    title: 'Files: recent files',
    description:
      'The Recent place of Files (#333): name with the file type icon, the location as a link to the folder, when it was opened and the size. Compare with f-files.',
    render: (context) => <RecentFilesScene context={context} />,
  },
  {
    id: 'file-dialog-save',
    group: 'patterns',
    title: 'App file dialog: save',
    description:
      'The dialog an app opens with files.saveAs (#324): the places with a lock on read-only folders, up and the path in a field, the folder as a table, the file name below. Compare with f-files.',
    render: (context) => (
      <FileDialogScene
        context={context}
        request={{ kind: 'save', multiple: false, extensions: [], suggestedName: 'Notes.md' }}
      />
    ),
  },
  {
    id: 'file-dialog-pick',
    group: 'patterns',
    title: 'App file dialog: choose',
    description:
      'The dialog of files.pick with several files and a type filter: only the pictures and folders are listed, and the line below says which types.',
    render: (context) => (
      <FileDialogScene
        context={context}
        request={{ kind: 'pick', multiple: true, extensions: ['jpg', 'png'], suggestedName: '' }}
      />
    ),
  },
  ...storageScreens,
  ...appScreens,
];
