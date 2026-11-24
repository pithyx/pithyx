import type { components } from '@pithyx/api-client';
import type { QueryClient } from '@tanstack/react-query';
import { queryKeys } from '../api/queries';
import { BackupApp } from '../apps/backup/BackupApp';
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
  sampleMatrix,
  samplePicture,
  sampleTrashed,
  storeAnswers,
} from './appSamples';
import {
  backupAnswers,
  backupEmptyAnswers,
  backupNoJobsAnswers,
  seedRunningBackup,
} from './backupSample';
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
    id: 'settings-notification-matrix',
    group: 'apps',
    title: 'Settings: notification events',
    description:
      "Every event the box reports by the administrators' desktop and the box's channels, grouped by category; a box applies at once.",
    render: settings('notifications', [{ press: 'settings:notifications.matrix.tab' }], {
      'internal.admin.notifications.matrix': () => sampleMatrix,
      'internal.admin.notifications.channels': () => ({ items: [], emailAvailable: true }),
      'internal.admin.notifications.smtp': () => ({
        configured: false,
        host: null,
        port: 587,
        security: 'startTls',
        username: null,
        passwordSet: false,
        from: null,
      }),
    }),
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
  {
    id: 'backup-overview',
    group: 'apps',
    title: 'Backup: overview',
    description:
      'The first page: protected data, the runs of 30 days, the next run and the space on the targets; the month in a calendar with the day of the latest failure picked, the run going on now, what needs attention and the data backed up per day.',
    render: (context) => (
      <CoreAppScene
        context={context}
        app="backup"
        Component={BackupApp}
        answers={backupAnswers}
        seed={seedRunningBackup}
      />
    ),
  },
  {
    id: 'backup-jobs',
    group: 'apps',
    title: 'Backup: box backups',
    description:
      'Three backups from this box as a table with route, schedule, last and next run, size and the last 14 days: one backing up now, one fine and selected with its details and recent runs, one overdue after a failed run.',
    render: (context) => (
      <CoreAppScene
        context={context}
        app="backup"
        Component={BackupApp}
        answers={backupAnswers}
        seed={seedRunningBackup}
        script={[{ press: 'backup:nav.jobs' }, { press: 'Documents' }]}
      />
    ),
  },
  {
    id: 'backup-job-versions',
    group: 'apps',
    title: 'Backup: versions of a backup',
    description:
      'The page of one backup on its Versions tab: the size and added data per version as a chart, the versions with the reasons the retention kept them, one protected by hand, and the newest selected with what changed against the one before.',
    render: (context) => (
      <CoreAppScene
        context={context}
        app="backup"
        Component={BackupApp}
        answers={backupAnswers}
        seed={seedRunningBackup}
        script={[
          { press: 'backup:nav.jobs' },
          { press: 'Documents' },
          { press: 'backup:jobs.open' },
          { press: 'backup:detail.tab.versions' },
        ]}
      />
    ),
  },
  {
    id: 'backup-restore',
    group: 'apps',
    title: 'Backup: restore',
    description:
      'The restore page opened from a version of Documents: the backup and version, the versions on a timeline, a folder of the version with what changed against the one before, and a file selected with its versions on the box and in the backup.',
    render: (context) => (
      <CoreAppScene
        context={context}
        app="backup"
        Component={BackupApp}
        answers={backupAnswers}
        seed={seedRunningBackup}
        script={[
          { press: 'backup:nav.jobs' },
          { press: 'Documents' },
          { press: 'backup:jobs.open' },
          { press: 'backup:detail.tab.versions' },
          { press: 'backup:versions.browse' },
          { open: 'Documents' },
          { open: 'Taxes' },
          { open: '2025' },
          { press: 'tax-return-2025.pdf' },
        ]}
      />
    ),
  },
  {
    id: 'backup-activity',
    group: 'apps',
    title: 'Backup: activity',
    description:
      'The run going on now with its progress, speed and Cancel, the history of every run with filters and CSV export, and a run with warnings selected: the files it could not back up and why.',
    render: (context) => (
      <CoreAppScene
        context={context}
        app="backup"
        Component={BackupApp}
        answers={backupAnswers}
        seed={seedRunningBackup}
        script={[{ press: 'backup:nav.activity' }, { press: 'backup:runs.states.warning' }]}
      />
    ),
  },
  {
    id: 'backup-empty',
    group: 'apps',
    title: 'Backup: first start',
    description:
      'The box backups of a box without a target: the empty list says a backup needs a target first and offers to add one.',
    render: (context) => (
      <CoreAppScene
        context={context}
        app="backup"
        Component={BackupApp}
        answers={backupEmptyAnswers}
        script={[{ press: 'backup:nav.jobs' }]}
      />
    ),
  },
  {
    id: 'backup-jobs-empty',
    group: 'apps',
    title: 'Backup: no backup yet',
    description:
      'The box backups of a box with targets but no backup: the empty list offers to add the first one.',
    render: (context) => (
      <CoreAppScene
        context={context}
        app="backup"
        Component={BackupApp}
        answers={backupNoJobsAnswers}
        script={[{ press: 'backup:nav.jobs' }]}
      />
    ),
  },
  {
    id: 'backup-targets-empty',
    group: 'apps',
    title: 'Backup: no target yet',
    description:
      'The targets page without a target: the empty list offers to add the first server.',
    render: (context) => (
      <CoreAppScene
        context={context}
        app="backup"
        Component={BackupApp}
        answers={backupEmptyAnswers}
        script={[{ press: 'backup:nav.targets' }]}
      />
    ),
  },
  {
    id: 'backup-job-new',
    group: 'apps',
    title: 'Backup: new box backup',
    description:
      'The dialog of a new box backup: target and repository, folders, schedule, versions to keep, the integrity check and limits.',
    render: (context) => (
      <CoreAppScene
        context={context}
        app="backup"
        Component={BackupApp}
        answers={backupAnswers}
        script={[
          { press: 'backup:nav.jobs' },
          { press: 'backup:jobs.add' },
          { show: 'backup:jobForm.addTitle' },
        ]}
      />
    ),
  },
  {
    id: 'backup-targets',
    group: 'apps',
    title: 'Backup: targets',
    description:
      'Two SFTP targets, each with its own key and the host key the administrator confirmed.',
    render: (context) => (
      <CoreAppScene
        context={context}
        app="backup"
        Component={BackupApp}
        answers={backupAnswers}
        script={[{ press: 'backup:nav.targets' }]}
      />
    ),
  },
];
