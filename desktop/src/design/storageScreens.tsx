import { StorageApp } from '../apps/storage/StorageApp';
import type { ScreenDefinition } from './screens';
import { CoreAppScene, type ScriptStep } from './CoreAppScene';

function storage(section: string, script: ScriptStep[] = []): ScreenDefinition['render'] {
  return (context) => (
    <CoreAppScene
      context={context}
      app="storage"
      Component={StorageApp}
      launch={{ section }}
      script={script}
    />
  );
}

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
    id: 'storage-snapshots',
    group: 'storage',
    title: 'Storage: snapshots',
    description:
      'Snapshots of a shared folder with schedules and retention, a held snapshot and one Pithyx did not take.',
    render: storage('snapshots'),
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
    id: 'storage-volume-create',
    group: 'storage',
    title: 'Create a volume',
    description: 'A block device for a virtual machine, thick or sparse, with its exact command.',
    render: storage('datasets', [
      { press: 'storage:datasets.volume.button', inside: 'tank/pithyx/volumes/vm-disk' },
      { fill: 'storage:datasets.volume.name', value: 'windows-11' },
      { fill: 'storage:datasets.volume.size', value: '128' },
      { press: 'storage:create.preview' },
    ]),
  },
];
