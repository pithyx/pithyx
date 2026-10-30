import type { components } from '@pithyx/api-client';
import type { QueryClient } from '@tanstack/react-query';
import { queryKeys } from '../api/queries';
import { FilesApp } from '../apps/files/FilesApp';
import { LogsApp } from '../apps/logs/LogsApp';
import { MonitorApp } from '../apps/monitor/MonitorApp';
import { PreferencesApp } from '../apps/preferences/PreferencesApp';
import { SettingsApp } from '../apps/settings/SettingsApp';
import { StoreApp } from '../apps/store/StoreApp';
import type { BackupSummary } from '../apps/sdk/backup';
import { trustQuery } from '../security/trust';
import {
  filesSample,
  homesAnswers,
  logsAnswers,
  monitorEvents,
  sampleFolder,
  sampleLogEntries,
  samplePicture,
  sampleTrashed,
  storeAnswers,
} from './appSamples';
import { CoreAppScene, type ScriptStep } from './CoreAppScene';
import { sampleNetwork, type ExtraAnswers } from './storageSample';
import type { ScreenDefinition } from './screens';

const files = (session: object, script: ScriptStep[] = []): ScreenDefinition['render'] =>
  function FilesScreen(context) {
    return (
      <CoreAppScene
        context={context}
        app="files"
        Component={FilesApp}
        options={{ files: filesSample, session }}
        script={script}
      />
    );
  };

const monitor = (script: ScriptStep[] = []): ScreenDefinition['render'] =>
  function MonitorScreen(context) {
    return (
      <CoreAppScene
        context={context}
        app="monitor"
        Component={MonitorApp}
        options={{ events: monitorEvents }}
        script={script}
      />
    );
  };

const store = (page: string): ScreenDefinition['render'] =>
  function StoreScreen(context) {
    return (
      <CoreAppScene
        context={context}
        app="store"
        Component={StoreApp}
        launch={{ page }}
        answers={storeAnswers}
      />
    );
  };

// The signed-in user of Personal settings, which reads the session and the device's trust from the daemon itself.
const sessionUser: components['schemas']['UserInfo'] = {
  id: 1,
  username: 'ada',
  displayName: 'Ada Admin',
  isAdmin: true,
  totpEnabled: true,
  groups: [{ id: 1, name: 'administrators' }],
  permissions: { admin: true, apps: [], protocols: ['smb', 'sftp'], rights: [] },
};

const seedPreferences = (totpEnabled: boolean) => (client: QueryClient) => {
  client.setQueryData(queryKeys.session, { ...sessionUser, totpEnabled });
  client.setQueryData(trustQuery.queryKey, 'trusted');
};

const preferences = (script: ScriptStep[] = [], totpEnabled = true): ScreenDefinition['render'] =>
  function PreferencesScreen(context) {
    return (
      <CoreAppScene
        context={context}
        app="preferences"
        Component={PreferencesApp}
        seed={seedPreferences(totpEnabled)}
        script={script}
      />
    );
  };

const settings = (
  page: string,
  script: ScriptStep[] = [],
  answers: ExtraAnswers = {},
): ScreenDefinition['render'] =>
  function SettingsScreen(context) {
    return (
      <CoreAppScene
        context={context}
        app="settings"
        Component={SettingsApp}
        launch={{ page }}
        script={script}
        answers={answers}
      />
    );
  };

const twoFactorSetup: ScriptStep[] = [
  { press: 'desktop:preferences.pages.security' },
  { press: 'desktop:menu.twoFactor' },
  { press: 'auth:totpSetup.start' },
];

const checkedBackup: BackupSummary = {
  restoreId: 'sample',
  createdAt: '2026-09-25T18:40:00Z',
  hostName: 'pithyx',
  version: '1.2.0',
  users: 4,
  groups: 2,
  sharedFolders: 3,
  categories: [
    'users',
    'sharedFolders',
    'storage',
    'system',
    'certificates',
    'apps',
    'reverseProxy',
  ],
};

// A change applied a few seconds ago: the countdown starts at 55 seconds whenever the screen is opened.
const pendingNetwork: ExtraAnswers = {
  'internal.admin.network.get': () => ({
    ...sampleNetwork,
    pending: {
      id: 'sample',
      deadline: new Date(Date.now() + 55_500).toISOString(),
      hostName: null,
      interfaces: ['enp1s0'],
      addresses: [],
      origin: 'web',
    },
  }),
};

const firstWarning = sampleLogEntries[0]?.message ?? '';

// The core apps of the design pass (#227) on sample data (appSamples.ts), compared with the mockups of direction F.
export const appScreens: ScreenDefinition[] = [
  {
    id: 'files-folder',
    group: 'apps',
    title: 'Files: a folder',
    description:
      'Files in a folder of holiday photos: the folder tree with the shares and the recycle bin, the toolbar with the primary action first, the sortable list and the details pane of the selected photo. Compare with f-files.',
    render: files({ share: '2', path: sampleFolder, select: samplePicture }, [
      { press: 'files:details' },
    ]),
  },
  {
    id: 'files-trash',
    group: 'apps',
    title: 'Files: recycle bin',
    description:
      'The recycle bin of a shared folder with two deleted photos, where they were, who deleted them and when they go for good.',
    render: files({ share: '2', path: '', view: 'trash' }, [{ press: sampleTrashed }]),
  },
  {
    id: 'logs-center',
    group: 'apps',
    title: 'Log Center',
    description:
      'Sources grouped into System and Apps with their warnings and errors, the levels with counts, entries grouped by day and the details of the selected entry. Compare with f-logs.',
    render: (context) => (
      <CoreAppScene
        context={context}
        app="logs"
        Component={LogsApp}
        answers={logsAnswers}
        script={[{ press: firstWarning }]}
      />
    ),
  },
  {
    id: 'monitor-overview',
    group: 'apps',
    title: 'Resource Monitor: overview',
    description:
      'CPU with every core, memory with the ZFS cache, network and disks during a scrub, temperatures and the processes that use the most CPU. Compare with f-monitor.',
    render: monitor([{ check: 'monitor:range.hour' }]),
  },
  {
    id: 'monitor-processes',
    group: 'apps',
    title: 'Resource Monitor: processes',
    description: 'Every process as a sortable table with its user, CPU and memory.',
    render: monitor([{ check: 'monitor:range.hour' }, { press: 'monitor:views.processes' }]),
  },
  {
    id: 'store-discover',
    group: 'apps',
    title: 'Store: discover',
    description:
      'The newest installable app as the featured one, then new and updated apps as cards. Compare with f-store.',
    render: store('discover'),
  },
  {
    id: 'store-updates',
    group: 'apps',
    title: 'Store: updates',
    description:
      'An update with its release notes, and the update settings: policy, daily check time and channel.',
    render: store('updates'),
  },
  {
    id: 'preferences-account',
    group: 'apps',
    title: 'Personal settings: account',
    description:
      'The window of personal settings with its pages in the navigation and the account of the signed-in user.',
    render: preferences(),
  },
  {
    id: 'preferences-appearance',
    group: 'apps',
    title: 'Personal settings: appearance',
    description: 'Theme, accent color, wallpaper and taskbar position, which apply at once.',
    render: preferences([{ press: 'desktop:preferences.pages.appearance' }]),
  },
  {
    id: 'files-conflict',
    group: 'apps',
    title: 'Files: names already exist',
    description:
      'An upload of a photo the folder already has: the dialog asks whether to skip, replace or keep both.',
    // The folder has to be there before the upload, so the photo is selected first.
    render: files({ share: '2', path: sampleFolder }, [
      { press: samplePicture },
      { upload: samplePicture },
    ]),
  },
  {
    id: 'preferences-two-factor',
    group: 'apps',
    title: 'Personal settings: two-factor setup',
    description:
      'Turning on two-factor sign-in: the QR code, the key to type by hand and the first code.',
    render: preferences(twoFactorSetup, false),
  },
  {
    id: 'preferences-recovery-codes',
    group: 'apps',
    title: 'Personal settings: recovery codes',
    description:
      'The ten recovery codes, shown once after the first code, with the button to copy them.',
    render: preferences(
      [
        ...twoFactorSetup,
        { fill: 'auth:totpSetup.code', value: '492817' },
        { press: 'auth:totpSetup.confirm' },
      ],
      false,
    ),
  },
  {
    id: 'preferences-language',
    group: 'apps',
    title: 'Personal settings: language',
    description: 'The language of this device: automatic, English or German.',
    render: preferences([{ press: 'desktop:preferences.pages.language' }]),
  },
  {
    id: 'settings-backup-checked',
    group: 'apps',
    title: 'Settings: a checked backup',
    description:
      'A backup file after Check backup: when and where it was made, what it holds and the parts to restore.',
    render: settings(
      'backup',
      [
        { upload: 'pithyx-2026-09-25.pithyxbackup' },
        {
          fill: 'settings:backup.restore.password',
          inside: 'settings:backup.restore.title',
          value: 'sample password',
        },
        { press: 'settings:backup.restore.check' },
        { show: 'settings:backup.restore.title' },
      ],
      { 'internal.admin.backup.inspect': () => checkedBackup },
    ),
  },
  {
    id: 'settings-network-confirm',
    group: 'apps',
    title: 'Settings: confirm the network',
    description:
      'Right after a network change: the countdown with Keep and Undo now, and the box goes back without them.',
    render: settings('network', [], pendingNetwork),
  },
  {
    id: 'settings-homes',
    group: 'apps',
    title: 'Settings: home folders',
    description:
      'Every home folder as a table with its pool, use and limit, and the default limit.',
    render: (context) => (
      <CoreAppScene
        context={context}
        app="settings"
        Component={SettingsApp}
        launch={{ page: 'homes' }}
        answers={homesAnswers}
      />
    ),
  },
];
