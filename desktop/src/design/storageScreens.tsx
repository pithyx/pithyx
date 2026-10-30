import { SettingsApp } from '../apps/settings/SettingsApp';
import { CoreAppScene, type ScriptStep } from './CoreAppScene';
import type { ScreenDefinition } from './screens';
import type { ExtraAnswers } from './storageSample';
import { samplePoolsExpanding, samplePoolsWithPreview } from './storageSample';

// Storage is a group of Settings (D402); the datasets open from its advanced page.
const storagePages = {
  pools: 'pools',
  disks: 'disks',
  maintenance: 'maintenance',
  advanced: 'storageAdvanced',
  datasets: 'storageAdvanced',
} as const;

function storage(
  section: keyof typeof storagePages,
  script: ScriptStep[] = [],
  answers: ExtraAnswers = {},
): ScreenDefinition['render'] {
  const opening: ScriptStep[] =
    section === 'datasets' ? [{ press: 'storage:advanced.datasets.button' }] : [];
  return (context) => (
    <CoreAppScene
      context={context}
      app="settings"
      Component={SettingsApp}
      launch={{ page: storagePages[section] }}
      script={[...opening, ...script]}
      answers={answers}
    />
  );
}

function settings(page: string | null, script: ScriptStep[] = []): ScreenDefinition['render'] {
  return (context) => (
    <CoreAppScene
      context={context}
      app="settings"
      Component={SettingsApp}
      launch={page === null ? null : { page }}
      script={script}
    />
  );
}

const withPreview: ExtraAnswers = {
  'internal.admin.storage.pools': () => samplePoolsWithPreview,
};

const wizardDisks: ScriptStep[] = [
  { press: 'storage:create.button' },
  { check: 'sde' },
  { check: 'sdf' },
  {
    fill: 'storage:create.confirm',
    params: { disk: 'sdf', serial: 'WX41A9QZ' },
    value: 'WX41A9QZ',
  },
];
const wizardLayout: ScriptStep[] = [...wizardDisks, { press: 'storage:wizard.next' }];
const wizardReview: ScriptStep[] = [
  ...wizardLayout,
  { press: 'storage:wizard.next' },
  { press: 'storage:wizard.encryption.none' },
  { press: 'storage:wizard.next' },
  { fill: 'storage:create.name', value: 'tank2' },
  { press: 'storage:create.preview' },
];

// Real Storage components on sample data (storageSample.ts); the scripts open the dialog each screen shows.
export const storageScreens: ScreenDefinition[] = [
  {
    id: 'storage-pools',
    group: 'storage',
    title: 'Storage: pools',
    description:
      'Pool cards with layout, health, fill level and the last scrub; a degraded mirror with a missing disk and its repair actions.',
    render: storage('pools'),
  },
  {
    id: 'storage-disks',
    group: 'storage',
    title: 'Storage: disks',
    description:
      "Every disk with the guard's verdict for a new pool and its SMART health: system disk, pool members, an empty disk, one with old data.",
    render: storage('disks'),
  },
  {
    id: 'storage-datasets',
    group: 'storage',
    title: 'Storage: datasets and volumes',
    description:
      'Datasets and volumes per pool with used and free space, limit, compression and folder, and the actions each one allows.',
    render: storage('datasets'),
  },
  {
    id: 'storage-advanced',
    group: 'storage',
    title: 'Storage: advanced',
    description:
      'Volumes, exporting and importing a pool and the datasets table, each with a sentence on what it does and when.',
    render: storage('advanced'),
  },
  {
    id: 'storage-maintenance',
    group: 'storage',
    title: 'Storage: health and maintenance',
    description:
      'Alerts (one muted), scrub schedules per pool with the next run, SMART self-tests.',
    render: storage('maintenance'),
  },
  {
    id: 'storage-wizard-disks',
    group: 'storage',
    title: 'Pool wizard: disks',
    description:
      'Choosing the disks; a disk with data needs its serial number typed, the system disk is not offered.',
    render: storage('pools', wizardDisks),
  },
  {
    id: 'storage-wizard-layout',
    group: 'storage',
    title: 'Pool wizard: layout',
    description:
      'Layout cards with a drawing of data and protection disks, usable space (mixed sizes waste some), failures tolerated and the recommendation.',
    render: storage('pools', wizardLayout),
  },
  {
    id: 'storage-wizard-encryption',
    group: 'storage',
    title: 'Pool wizard: encryption',
    description:
      'Encryption with a key chosen, and how the pool unlocks: from the key vault at every start, by hand or from a USB stick.',
    render: storage('pools', [
      ...wizardLayout,
      { press: 'storage:wizard.next' },
      { check: 'storage:wizard.encryption.key' },
    ]),
  },
  {
    id: 'storage-wizard-review',
    group: 'storage',
    title: 'Pool wizard: name and commands',
    description:
      'The name and the exact commands, including clearing the disk with data, before anything runs.',
    render: storage('pools', wizardReview),
  },
  {
    id: 'storage-plan-running',
    group: 'storage',
    title: 'Plan running',
    description: 'A plan running as a job: the step it is on and the command it runs.',
    render: storage('pools', [...wizardReview, { press: 'storage:wizard.create' }]),
  },
  {
    id: 'storage-replace',
    group: 'storage',
    title: 'Replace a disk',
    description:
      'Replacing the missing disk of the degraded mirror: the new disk is always confirmed with its serial number.',
    render: storage('pools', [
      { press: 'storage:actions.replace.button', inside: 'WX40B3MM' },
      { press: 'sde' },
      {
        fill: 'storage:create.confirm',
        params: { disk: 'sde', serial: 'ZA1B2D7H' },
        value: 'ZA1B2D7H',
      },
      { press: 'storage:create.preview' },
    ]),
  },
  {
    id: 'storage-destroy',
    group: 'storage',
    title: 'Destroy a pool',
    description: 'Destroying needs the pool name typed and lists every dataset that goes.',
    render: storage('pools', [
      { press: 'storage:actions.destroy.button', inside: 'backup' },
      { fill: 'storage:actions.destroy.confirm', params: { pool: 'backup' }, value: 'backup' },
      { press: 'storage:create.preview' },
    ]),
  },
  {
    id: 'storage-dataset-limits',
    group: 'storage',
    title: 'Dataset limits and compression',
    description:
      'Quota, reservation, compression and record size of a shared folder, with the exact zfs set.',
    render: storage('datasets', [
      { press: 'storage:datasets.limits.button', inside: 'tank/pithyx/shares/media' },
      { fill: 'storage:datasets.limits.quota', value: '6' },
      { select: 'storage:datasets.limits.compression', value: 'zstd-3' },
      { press: 'storage:create.preview' },
    ]),
  },
  {
    id: 'storage-dataset-delete',
    group: 'storage',
    title: 'Delete a dataset',
    description:
      'Deleting needs the full name typed and lists the dataset with every snapshot that goes with it.',
    render: storage('datasets', [
      { press: 'storage:datasets.destroy.button', inside: 'tank/pithyx/shares/family' },
      {
        fill: 'storage:datasets.destroy.confirm',
        params: { dataset: 'tank/pithyx/shares/family' },
        value: 'tank/pithyx/shares/family',
      },
      { press: 'storage:create.preview' },
    ]),
  },
  {
    id: 'storage-import-scan',
    group: 'storage',
    title: 'Import a pool: scan',
    description:
      'The scan runs only when asked; an importable mirror with its disks, and a pool that cannot be imported with what ZFS reports.',
    render: storage('advanced', [
      { press: 'storage:import.button' },
      { press: 'storage:import.scan' },
      { press: 'photos' },
    ]),
  },
  {
    id: 'storage-import-readonly',
    group: 'storage',
    title: 'Import a pool: read-only first',
    description:
      'The read-only import with its exact command: nothing is mounted and nothing changes.',
    render: storage('advanced', [
      { press: 'storage:import.button' },
      { press: 'storage:import.scan' },
      { press: 'photos' },
      { press: 'storage:create.preview' },
    ]),
  },
  {
    id: 'storage-import-preview-card',
    group: 'storage',
    title: 'Imported read-only',
    description:
      'A pool imported read-only for a look at its datasets, with the two ways on: import for use or export again.',
    render: storage('pools', [], withPreview),
  },
  {
    id: 'storage-import-for-use',
    group: 'storage',
    title: 'Import for use',
    description:
      'Which datasets are mounted and which stay unmounted because they would cover a system folder.',
    render: storage(
      'pools',
      [
        { press: 'storage:change.import.button', inside: 'photos' },
        { press: 'storage:create.preview' },
      ],
      withPreview,
    ),
  },
  {
    id: 'storage-export',
    group: 'storage',
    title: 'Export a pool',
    description: 'Exporting lists every dataset that becomes unavailable.',
    render: storage('advanced', [
      { press: 'storage:advanced.export.button', params: { pool: 'backup' } },
      { press: 'storage:create.preview' },
    ]),
  },
  {
    id: 'storage-add-disks',
    group: 'storage',
    title: 'Add disks to a pool',
    description:
      'A single disk added to a RAID-Z1 lowers its protection and cannot be removed again: both are explained, and the pool name must be typed.',
    render: storage('pools', [
      { press: 'storage:change.add.button', inside: 'tank' },
      { check: 'sde' },
      { press: 'storage:create.preview' },
      { fill: 'storage:confirmName.label', params: { pool: 'tank' }, value: 'tank' },
      { press: 'storage:create.preview' },
    ]),
  },
  {
    id: 'storage-expand',
    group: 'storage',
    title: 'Expand a RAID-Z',
    description:
      'One more disk for the RAID-Z1 of tank: irreversible, with the typed name and the exact command.',
    render: storage('pools', [
      { press: 'storage:change.expand.button', inside: 'tank' },
      { press: 'sde' },
      { fill: 'storage:confirmName.label', params: { pool: 'tank' }, value: 'tank' },
      { press: 'storage:create.preview' },
    ]),
  },
  {
    id: 'storage-expanding',
    group: 'storage',
    title: 'RAID-Z expansion running',
    description: 'The pool card while ZFS spreads the data over the added disk.',
    render: storage('pools', [], {
      'internal.admin.storage.pools': () => samplePoolsExpanding,
    }),
  },
  {
    id: 'settings-overview',
    group: 'storage',
    title: 'Settings: overview',
    description:
      'System settings as tiles in three categories with a search; personal settings are in the account menu.',
    render: settings(null),
  },
  {
    id: 'settings-info',
    group: 'storage',
    title: 'Settings: Info Center',
    description: 'Version and commit, operating system, machine, processor, memory and uptime.',
    render: settings('info'),
  },
  {
    id: 'settings-network',
    group: 'storage',
    title: 'Settings: network',
    description:
      'Every interface with its addresses, DHCP or static, gateway and name servers; read only until the confirmation timer.',
    render: settings('network'),
  },
  {
    id: 'settings-users',
    group: 'storage',
    title: 'Settings: users and groups',
    description:
      'Users as a table with groups, file services, state and two-factor sign-in, a toolbar with the primary action first and the selected user on the right.',
    render: settings('users', [{ press: 'Carla Müller-Schmidt' }]),
  },
  {
    id: 'settings-groups',
    group: 'storage',
    title: 'Settings: groups',
    description: 'The groups tab with members and the builtin administrators group.',
    render: settings('users', [{ press: 'users:groups.title' }]),
  },
  {
    id: 'settings-file-services',
    group: 'storage',
    title: 'Settings: file services',
    description:
      'SMB, SFTP, WebDAV, FTP, SSH and the firewall as sections with their state as a badge in the title row.',
    render: settings('fileServices'),
  },
  {
    id: 'settings-ups',
    group: 'storage',
    title: 'Settings: UPS',
    description: 'The state a UPS on USB reports, the kind of UPS as radios and the limits.',
    render: settings('ups'),
  },
  {
    id: 'settings-regional',
    group: 'storage',
    title: 'Settings: regional settings',
    description:
      'System language and formats with an example, then the time zone and the time servers.',
    render: settings('regional'),
  },
  {
    id: 'settings-shares',
    group: 'storage',
    title: 'Settings: shared folders',
    description:
      'Every shared folder with its pool, space or quota, description, rights and the restore point switch.',
    render: settings('shares'),
  },
  {
    id: 'settings-restore-points',
    group: 'storage',
    title: 'What are restore points?',
    description:
      'Restore points in plain words: what they cost, when a deleted file frees its space, what they protect against.',
    render: settings('shares', [{ press: 'settings:restorePoints.explain' }]),
  },
  {
    id: 'settings-share-create',
    group: 'storage',
    title: 'Create a shared folder',
    description: 'Name, pool and description, then the dataset it becomes and the exact command.',
    render: settings('shares', [
      { press: 'settings:shares.create' },
      { fill: 'settings:shares.createDialog.name', value: 'Holiday photos' },
      { fill: 'settings:shares.createDialog.description', value: 'Pictures from every trip' },
      { press: 'settings:shares.createDialog.preview' },
    ]),
  },
  {
    id: 'settings-share-edit',
    group: 'storage',
    title: 'Edit a shared folder',
    description:
      'Name and description, and the rights of every user: none, read, or read and write.',
    render: settings('shares', [{ press: 'Family' }, { press: 'settings:shares.edit' }]),
  },
  {
    id: 'settings-share-delete',
    group: 'storage',
    title: 'Delete a shared folder',
    description:
      'Deleting asks what happens with the data; deleting it for good lists the dataset and every snapshot.',
    render: settings('shares', [
      { press: 'settings:shares.delete', inside: 'Family' },
      { press: 'settings:shares.deleteDialog.destroy' },
      { fill: 'settings:shares.deleteDialog.confirm', params: { name: 'Family' }, value: 'Family' },
      { press: 'settings:shares.deleteDialog.preview' },
    ]),
  },
];
