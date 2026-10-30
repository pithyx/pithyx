import type { components } from '@pithyx/api-client';
import type { QueryClient } from '@tanstack/react-query';
import { queryKeys } from '../api/queries';
import { FilesApp } from '../apps/files/FilesApp';
import { LogsApp } from '../apps/logs/LogsApp';
import { MonitorApp } from '../apps/monitor/MonitorApp';
import { PreferencesApp } from '../apps/preferences/PreferencesApp';
import { SettingsApp } from '../apps/settings/SettingsApp';
import { StoreApp } from '../apps/store/StoreApp';
import { trustQuery } from '../security/trust';
import {
  filesSample,
  homesAnswers,
  logsAnswers,
  monitorEvents,
  sampleLogEntries,
  storeAnswers,
} from './appSamples';
import { CoreAppScene, type ScriptStep } from './CoreAppScene';
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

function seedPreferences(client: QueryClient) {
  client.setQueryData(queryKeys.session, sessionUser);
  client.setQueryData(trustQuery.queryKey, 'trusted');
}

const preferences = (script: ScriptStep[] = []): ScreenDefinition['render'] =>
  function PreferencesScreen(context) {
    return (
      <CoreAppScene
        context={context}
        app="preferences"
        Component={PreferencesApp}
        seed={seedPreferences}
        script={script}
      />
    );
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
    render: files({ share: '2', path: '2026-summer', select: 'Lighthouse.jpg' }, [
      { press: 'files:details' },
    ]),
  },
  {
    id: 'files-trash',
    group: 'apps',
    title: 'Files: recycle bin',
    description:
      'The recycle bin of a shared folder with two deleted photos, where they were, who deleted them and when they go for good.',
    render: files({ share: '2', path: '', view: 'trash' }, [{ press: 'Blurry sunset.jpg' }]),
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
