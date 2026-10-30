// Sample storage for the design gallery: a box with a healthy RAID-Z1 pool, a degraded mirror, an empty disk and one
// with old data. The real Storage and Settings components render it through a stand-in for the SDK app, so the
// gallery shows exactly what the apps show. Everything here is user data or raw values, never interface text.
import { createFormatters, type PithyxApp } from '@pithyx/sdk';
import i18next from 'i18next';
import type {
  Dataset,
  Job,
  Pool,
  Snapshot,
  StorageDisk,
  StoragePlanPreview,
  StoragePlanRequest,
  Vdev,
} from '../apps/sdk/internal';
import type { UpdateStatus } from '../apps/sdk/updates';
import type { UpsStatus } from '../apps/sdk/ups';
import type { GroupSummary, UserSummary } from '../apps/sdk/users';
import type { FileServiceSettings } from '../apps/sdk/system';

const TB = 1_000_000_000_000;
const GB = 1_000_000_000;

function disk(
  name: string,
  serial: string,
  sizeBytes: number,
  model: string,
  guard: Pick<StorageDisk['guard'], 'decision' | 'reason'> & { pool?: string; contents?: string[] },
  rotational = true,
): StorageDisk {
  const id = `serial-${serial}`;
  return {
    disk: {
      id,
      name,
      path: `/dev/${name}`,
      model,
      serial,
      sizeBytes,
      rotational,
      removable: false,
      readOnly: false,
      transport: rotational ? 'sata' : 'nvme',
      vendor: null,
      wwn: null,
      partitions: [],
    },
    guard: {
      diskId: id,
      decision: guard.decision,
      reason: guard.reason,
      pool: guard.pool ?? null,
      contents: guard.contents ?? [],
    },
  };
}

export const sampleDisks: StorageDisk[] = [
  disk(
    'nvme0n1',
    'S5GXNF0R412345',
    500 * GB,
    'NVMe SSD 500GB',
    { decision: 'refused', reason: 'system_disk' },
    false,
  ),
  disk('sda', 'ZA1B2C3D', 8 * TB, 'NAS HDD 8TB', {
    decision: 'refused',
    reason: 'pool_member',
    pool: 'tank',
  }),
  disk('sdb', 'ZA1B2C4E', 8 * TB, 'NAS HDD 8TB', {
    decision: 'refused',
    reason: 'pool_member',
    pool: 'tank',
  }),
  disk('sdc', 'ZA1B2C5F', 8 * TB, 'NAS HDD 8TB', {
    decision: 'refused',
    reason: 'pool_member',
    pool: 'tank',
  }),
  disk('sdd', 'WX41A8KD', 4 * TB, 'NAS HDD 4TB', {
    decision: 'refused',
    reason: 'pool_member',
    pool: 'backup',
  }),
  disk('sde', 'ZA1B2D7H', 8 * TB, 'NAS HDD 8TB', { decision: 'allowed', reason: 'empty' }),
  disk('sdf', 'WX41A9QZ', 4 * TB, 'NAS HDD 4TB', {
    decision: 'needsSerialConfirmation',
    reason: 'has_data',
    contents: ['ext4'],
  }),
];

const byId = (name: string) => `/dev/disk/by-id/ata-NAS_HDD_${name}`;

function leaf(name: string, guid: string, state = 'ONLINE', errors = 0): Vdev {
  return {
    name: `ata-NAS_HDD_${name}`,
    vdevGuid: guid,
    missing: false,
    type: 'disk',
    role: 'data',
    state,
    path: byId(name),
    parityDisks: null,
    readErrors: 0,
    writeErrors: errors,
    checksumErrors: 0,
    children: [],
  };
}

export const samplePools: Pool[] = [
  {
    name: 'tank',
    poolGuid: '13915147893267678619',
    state: 'ONLINE',
    sizeBytes: 24 * TB,
    allocatedBytes: 10.1 * TB,
    freeBytes: 13.9 * TB,
    capacityPercent: 42,
    fragmentationPercent: 3,
    readOnly: false,
    dataErrors: 0,
    scan: {
      kind: 'scrub',
      state: 'finished',
      paused: false,
      startedAt: '2026-09-21T02:00:00Z',
      endedAt: '2026-09-21T09:42:00Z',
      toExamineBytes: 10.1 * TB,
      examinedBytes: 10.1 * TB,
      errors: 0,
    },
    expansion: null,
    vdevs: [
      {
        name: 'raidz1-0',
        vdevGuid: '9206321434375767675',
        missing: false,
        type: 'raidz',
        role: 'data',
        state: 'ONLINE',
        path: null,
        parityDisks: 1,
        readErrors: 0,
        writeErrors: 0,
        checksumErrors: 0,
        children: [
          leaf('ZA1B2C3D', '10269894157975282252'),
          leaf('ZA1B2C4E', '287319290319431821'),
          leaf('ZA1B2C5F', '11984350781056605882'),
        ],
      },
    ],
  },
  {
    name: 'backup',
    poolGuid: '17694517151681225246',
    state: 'DEGRADED',
    sizeBytes: 4 * TB,
    allocatedBytes: 2.7 * TB,
    freeBytes: 1.3 * TB,
    capacityPercent: 67,
    fragmentationPercent: 8,
    readOnly: false,
    dataErrors: 0,
    scan: null,
    expansion: null,
    vdevs: [
      {
        name: 'mirror-0',
        vdevGuid: '4147527772288846010',
        missing: false,
        type: 'mirror',
        role: 'data',
        state: 'DEGRADED',
        path: null,
        parityDisks: null,
        readErrors: 0,
        writeErrors: 0,
        checksumErrors: 0,
        children: [
          leaf('WX41A8KD', '1956317048960221727'),
          leaf('WX40B3MM', '9788833892966015473', 'UNAVAIL', 14),
        ],
      },
    ],
  },
];

function dataset(name: string, usedBytes: number, options: Partial<Dataset> = {}): Dataset {
  const pool = name.split('/')[0] ?? name;
  return {
    name,
    pool,
    type: 'filesystem',
    usedBytes,
    availableBytes: pool === 'tank' ? 9.2 * TB : 1.2 * TB,
    referencedBytes: usedBytes,
    mountPoint: null,
    mounted: false,
    encryption: 'off',
    keyStatus: null,
    encryptionRoot: null,
    quotaBytes: null,
    reservationBytes: null,
    compression: 'lz4',
    recordSizeBytes: 131072,
    volumeSizeBytes: null,
    volumeBlockSizeBytes: null,
    snapshotsUsedBytes: 0,
    ...options,
  };
}

export const sampleDatasets: Dataset[] = [
  dataset('backup', 2.7 * TB),
  dataset('backup/pithyx', 2.7 * TB),
  dataset('backup/pithyx/shares', 2.7 * TB),
  dataset('backup/pithyx/shares/archive', 2.7 * TB, {
    mountPoint: '/backup/archive',
    mounted: true,
  }),
  dataset('tank', 6.7 * TB),
  dataset('tank/pithyx', 6.7 * TB),
  dataset('tank/pithyx/apps', 12 * GB),
  dataset('tank/pithyx/homes', 310 * GB, { mountPoint: '/tank/homes', mounted: true }),
  dataset('tank/pithyx/shares', 6.3 * TB),
  dataset('tank/pithyx/shares/family', 1.4 * TB, {
    mountPoint: '/tank/family',
    mounted: true,
    quotaBytes: 2 * TB,
    snapshotsUsedBytes: 18 * GB,
  }),
  dataset('tank/pithyx/shares/media', 4.9 * TB, {
    mountPoint: '/tank/media',
    mounted: true,
    recordSizeBytes: 1048576,
    compression: 'zstd',
  }),
  dataset('tank/pithyx/system', 2 * GB, { mountPoint: '/tank/.pithyx/system', mounted: true }),
  dataset('tank/pithyx/volumes', 66 * GB),
  dataset('tank/pithyx/volumes/vm-disk', 66 * GB, {
    type: 'volume',
    recordSizeBytes: null,
    volumeSizeBytes: 64 * 1024 ** 3,
    volumeBlockSizeBytes: 16384,
  }),
];

const smart = (diskId: string, state: string, extra: Record<string, unknown> = {}) => ({
  diskId,
  state,
  asleep: false,
  checkedAt: '2026-09-26T14:00:00Z',
  readAt: '2026-09-26T14:00:00Z',
  reason: null,
  temperatureCelsius: 34,
  reallocatedSectors: 0,
  pendingSectors: 0,
  uncorrectableSectors: 0,
  mediaErrors: 0,
  criticalWarning: null,
  powerOnHours: 18_400,
  lastSelfTest: 'passed',
  ...extra,
});

export const sampleHealth = {
  alerts: [
    {
      key: 'pool.degraded:backup',
      code: 'storage.alert.poolDegraded',
      severity: 'critical',
      params: { pool: 'backup' },
      since: '2026-09-26T11:08:00Z',
      muted: false,
    },
    {
      key: 'disk.smart_warning:serial-WX41A8KD',
      code: 'storage.alert.smartWarning',
      severity: 'warning',
      params: { disk: 'sdd', pool: 'backup', reallocated: 8, pending: 0 },
      since: '2026-09-25T20:15:00Z',
      muted: true,
    },
  ],
  smart: [
    smart('serial-S5GXNF0R412345', 'passed', { temperatureCelsius: 41, lastSelfTest: null }),
    smart('serial-ZA1B2C3D', 'passed'),
    smart('serial-ZA1B2C4E', 'passed', { temperatureCelsius: 36 }),
    smart('serial-ZA1B2C5F', 'passed', { temperatureCelsius: 35 }),
    smart('serial-WX41A8KD', 'warning', { reallocatedSectors: 8, temperatureCelsius: 39 }),
    smart('serial-ZA1B2D7H', 'passed', { asleep: true, lastSelfTest: null }),
    smart('serial-WX41A9QZ', 'unknown', { readAt: null, reason: 'not_read' }),
  ],
};

export const sampleSchedules = {
  schedules: [
    {
      kind: 'scrub',
      target: 'tank',
      frequency: 'monthly',
      lastRunAt: '2026-09-21T02:00:00Z',
      nextRunAt: '2026-10-21T02:00:00Z',
    },
    {
      kind: 'scrub',
      target: 'backup',
      frequency: 'monthly',
      lastRunAt: null,
      nextRunAt: '2026-10-03T02:00:00Z',
    },
    {
      kind: 'shortSelfTest',
      target: '',
      frequency: 'weekly',
      lastRunAt: '2026-09-24T03:00:00Z',
      nextRunAt: '2026-10-01T03:00:00Z',
    },
    {
      kind: 'longSelfTest',
      target: '',
      frequency: 'monthly',
      lastRunAt: null,
      nextRunAt: '2026-10-12T03:00:00Z',
    },
  ],
};

function snapshot(
  name: string,
  createdAt: string,
  usedBytes: number,
  options: Partial<Snapshot> = {},
): Snapshot {
  const kind = /^pithyx-([a-z]+)-/.exec(name)?.[1] ?? null;
  return {
    dataset: 'tank/pithyx/shares/family',
    name,
    fullName: `tank/pithyx/shares/family@${name}`,
    createdAt,
    createTxg: Date.parse(createdAt) / 1000,
    holds: 0,
    pithyxKind: kind,
    referencedBytes: 1.4 * TB,
    usedBytes,
    ...options,
  };
}

export const sampleSnapshots: Snapshot[] = [
  snapshot('before-cleanup', '2026-09-12T18:03:00Z', 9 * GB, { pithyxKind: null }),
  snapshot('pithyx-weekly-20260921T000000Z', '2026-09-21T00:00:00Z', 4 * GB, { holds: 1 }),
  snapshot('pithyx-daily-20260925T000000Z', '2026-09-25T00:00:00Z', 1.2 * GB),
  snapshot('pithyx-daily-20260926T000000Z', '2026-09-26T00:00:00Z', 310_000_000),
  snapshot('pithyx-hourly-20260926T130000Z', '2026-09-26T13:00:00Z', 22_000_000),
  snapshot('pithyx-hourly-20260926T140000Z', '2026-09-26T14:00:00Z', 4_000_000),
];

const sampleSnapshotSchedules = {
  dataset: 'tank/pithyx/shares/family',
  schedules: [
    { kind: 'hourly', enabled: true, keep: 24 },
    { kind: 'daily', enabled: true, keep: 14 },
    { kind: 'weekly', enabled: true, keep: 8 },
    { kind: 'monthly', enabled: false, keep: 12 },
  ],
};

const command = (line: string) => {
  const [program = '', ...args] = line.split(' ');
  return { program, arguments: args };
};

function preview(lines: string[], extra: Partial<StoragePlanPreview> = {}): StoragePlanPreview {
  return {
    planId: 'design-plan',
    disks: [],
    commands: lines.map(command),
    datasets: [],
    expiresAt: '2026-09-26T14:42:00Z',
    requiresConfirmName: false,
    unmounted: [],
    warnings: [],
    ...extra,
  };
}

function devicePath(diskId: string | undefined): string {
  const found = sampleDisks.find(({ disk: entry }) => entry.id === diskId);
  return byId(found?.disk.serial ?? 'unknown');
}

/** The plan the daemon would answer, with the same commands its plan builders produce for this box. */
export function samplePlan(request: StoragePlanRequest): StoragePlanPreview {
  const disks = request.diskIds ?? [];
  const pool = request.pool;
  switch (request.action) {
    case 'pool.create': {
      const clears = disks
        .filter(
          (id) =>
            sampleDisks.find(({ disk: entry }) => entry.id === id)?.guard.reason === 'has_data',
        )
        .flatMap((id) => [`wipefs --all ${devicePath(id)}-part1 ${devicePath(id)}`]);
      const layout =
        request.layout === 'single' || request.layout === 'stripe'
          ? []
          : [request.layout ?? 'mirror'];
      return preview([
        ...clears,
        [
          'zpool create -o ashift=12 -O compression=lz4 -O xattr=sa -O acltype=posixacl -O atime=off -O normalization=formD -O canmount=off',
          pool,
          ...layout,
          ...disks.map(devicePath),
        ].join(' '),
        `zfs create -o canmount=off -o mountpoint=none ${pool}/pithyx`,
        `zfs create -o canmount=off -o mountpoint=/${pool} ${pool}/pithyx/shares`,
        `zfs create -o mountpoint=/${pool}/homes ${pool}/pithyx/homes`,
        `zfs create -o canmount=off -o mountpoint=/${pool}/.pithyx/apps ${pool}/pithyx/apps`,
        `zfs create -o mountpoint=/${pool}/.pithyx/system ${pool}/pithyx/system`,
        `zfs create -o mountpoint=/${pool}/.pithyx/cache -o compression=off -o atime=off -o quota=2560M -o com.sun:auto-snapshot=false ${pool}/pithyx/cache`,
      ]);
    }
    case 'pool.replace':
      return preview([`zpool replace ${pool} ${request.vdev ?? ''} ${devicePath(disks[0])}`]);
    case 'pool.destroy':
      return preview([`zpool destroy ${pool}`], {
        datasets: sampleDatasets.filter((entry) => entry.pool === pool).map((entry) => entry.name),
        requiresConfirmName: true,
      });
    case 'pool.export':
      return preview([`zpool export ${pool}`], {
        datasets: sampleDatasets.filter((entry) => entry.pool === pool).map((entry) => entry.name),
        warnings: ['datasets_unavailable'],
      });
    case 'pool.vdev.add': {
      const lowers = request.layout === 'single';
      const typed = request.confirmName === pool;
      const warnings = lowers ? ['lowers_redundancy', 'irreversible'] : ['irreversible'];
      if (!typed) {
        return preview([], { planId: null, commands: null, warnings, requiresConfirmName: true });
      }
      return preview(
        [
          [
            'zpool add',
            ...(lowers ? ['--allow-replication-mismatch'] : []),
            pool,
            ...(lowers ? [] : [request.layout ?? 'mirror']),
            ...disks.map(devicePath),
          ].join(' '),
        ],
        { warnings, requiresConfirmName: true },
      );
    }
    case 'pool.raidz.expand':
      return request.confirmName === pool
        ? preview([`zpool attach ${pool} ${request.vdev ?? ''} ${devicePath(disks[0])}`], {
            warnings: ['irreversible'],
            requiresConfirmName: true,
          })
        : preview([], {
            planId: null,
            commands: null,
            warnings: ['irreversible'],
            requiresConfirmName: true,
          });
    case 'pool.import.readonly':
      return preview([
        `zpool import -o readonly=on -o cachefile=none -N -d /dev/disk/by-id ${request.poolGuid ?? ''}${request.newName ? ` ${request.newName}` : ''}`,
      ]);
    case 'pool.import':
      return preview(
        [
          `zpool export ${pool}`,
          `zpool import -N -d /dev/disk/by-id 5561838205117473190 ${pool}`,
          `zfs mount ${pool}/photos`,
        ],
        {
          datasets: [`${pool}/photos`],
          unmounted: [
            { name: `${pool}/old-root`, mountPoint: '/usr/local', reason: 'system_path' },
          ],
          warnings: ['unmounted_datasets'],
        },
      );
    case 'dataset.rename': {
      const parent = request.dataset?.split('/').slice(0, -1).join('/') ?? '';
      return preview([`zfs rename ${request.dataset ?? ''} ${parent}/${request.newName ?? ''}`], {
        datasets: [request.dataset ?? ''],
      });
    }
    case 'dataset.set': {
      const parts = [
        request.quotaBytes === undefined || request.quotaBytes === null
          ? null
          : `quota=${request.quotaBytes === 0 ? 'none' : String(request.quotaBytes)}`,
        request.reservationBytes === undefined || request.reservationBytes === null
          ? null
          : `reservation=${request.reservationBytes === 0 ? 'none' : String(request.reservationBytes)}`,
        request.compression ? `compression=${request.compression}` : null,
        request.recordSizeBytes ? `recordsize=${String(request.recordSizeBytes)}` : null,
      ].filter((part): part is string => part !== null);
      return preview([['zfs set', ...parts, request.dataset ?? ''].join(' ')]);
    }
    case 'dataset.destroy': {
      const name = request.dataset ?? '';
      const snapshots =
        name === 'tank/pithyx/shares/family'
          ? sampleSnapshots.map((entry) => entry.fullName ?? '')
          : [];
      return preview([`zfs destroy${snapshots.length > 0 ? ' -r' : ''} ${name}`], {
        datasets: [name, ...snapshots],
        requiresConfirmName: true,
      });
    }
    case 'snapshot.destroy':
      return preview([`zfs destroy ${request.dataset ?? ''}@${request.name ?? ''}`], {
        datasets: [`${request.dataset ?? ''}@${request.name ?? ''}`],
      });
    case 'snapshot.rollback': {
      const target = sampleSnapshots.find((entry) => entry.name === request.name);
      const newer = sampleSnapshots
        .filter((entry) => target !== undefined && entry.createTxg > target.createTxg)
        .map((entry) => entry.fullName ?? '');
      return preview(
        [
          `zfs rollback${newer.length > 0 ? ' -r' : ''} ${request.dataset ?? ''}@${request.name ?? ''}`,
        ],
        {
          datasets: newer,
          requiresConfirmName: true,
          warnings: newer.length > 0 ? ['changes_lost', 'newer_snapshots'] : ['changes_lost'],
        },
      );
    }
    case 'volume.create':
      return preview([
        `zfs create -V ${String(request.sizeBytes ?? 0)}${request.sparse ? ' -s' : ''} -o volblocksize=16K ${pool}/pithyx/volumes/${request.name ?? ''}`,
      ]);
    default:
      return preview([`zfs create ${pool}/pithyx/shares/${request.name ?? ''}`]);
  }
}

function at<T>(items: readonly T[], index: number): T {
  const item = items[index];
  if (item === undefined) {
    throw new Error(`The sample has no entry ${String(index)}`);
  }
  return item;
}

const tankPool = at(samplePools, 0);
const backupPool = at(samplePools, 1);

const verdict = (name: string, reason = 'importable') => {
  const found = sampleDisks.find(({ disk: entry }) => entry.name === name);
  return {
    diskId: found?.disk.id ?? name,
    decision: 'allowed' as const,
    reason,
    pool: null,
    contents: ['zfs_member'],
  };
};

/** What a scan finds: a mirror from an earlier installation, and a pool whose second disk is missing. */
export const sampleImportScan = {
  pools: [
    {
      name: 'photos',
      poolGuid: '5561838205117473190',
      state: 'ONLINE',
      status: null,
      action: 'The pool can be imported using its name or numeric identifier.',
      messageId: null,
      canImport: true,
      needsForce: false,
      nameTaken: false,
      allowed: true,
      unknownDevices: [],
      disks: [verdict('sde'), verdict('sdf')],
      vdevs: [
        {
          name: 'mirror-0',
          state: 'ONLINE',
          detail: null,
          diskId: null,
          children: [
            {
              name: 'ata-NAS_HDD_ZA1B2D7H',
              state: 'ONLINE',
              detail: null,
              diskId: 'serial-ZA1B2D7H',
              children: [],
            },
            {
              name: 'ata-NAS_HDD_WX41A9QZ',
              state: 'ONLINE',
              detail: null,
              diskId: 'serial-WX41A9QZ',
              children: [],
            },
          ],
        },
      ],
    },
    {
      name: 'old-backup',
      poolGuid: '8810472238916450321',
      state: 'UNAVAIL',
      status: 'One or more devices are missing from the system.',
      action: 'The pool cannot be imported. Attach the missing devices and try again.',
      messageId: 'ZFS-8000-3C',
      canImport: false,
      needsForce: false,
      nameTaken: false,
      allowed: false,
      unknownDevices: [],
      disks: [],
      vdevs: [
        {
          name: 'mirror-0',
          state: 'UNAVAIL',
          detail: 'insufficient replicas',
          diskId: null,
          children: [
            {
              name: '14120317431289201184',
              state: 'UNAVAIL',
              detail: 'cannot open',
              diskId: null,
              children: [],
            },
            {
              name: '2230098715612230871',
              state: 'UNAVAIL',
              detail: 'cannot open',
              diskId: null,
              children: [],
            },
          ],
        },
      ],
    },
  ],
};

/** The pools after the read-only import of `photos`, for the preview card and the import for use. */
export const samplePoolsWithPreview = {
  pools: [
    ...samplePools,
    {
      ...backupPool,
      name: 'photos',
      poolGuid: '5561838205117473190',
      state: 'ONLINE',
      readOnly: true,
      capacityPercent: 31,
      allocatedBytes: 2.5 * TB,
      freeBytes: 5.5 * TB,
      sizeBytes: 8 * TB,
      vdevs: [
        {
          ...at(backupPool.vdevs, 0),
          state: 'ONLINE',
          children: [
            leaf('ZA1B2D7H', '3411908277104522671'),
            leaf('WX41A9QZ', '7203318841902774120'),
          ],
        },
      ],
    },
  ] as Pool[],
  datasets: [
    ...sampleDatasets,
    dataset('photos', 2.5 * TB, { mountPoint: '/photos' }),
    dataset('photos/2019', 0.9 * TB, { mountPoint: '/photos/2019' }),
    dataset('photos/2020', 1.6 * TB, { mountPoint: '/photos/2020' }),
    dataset('photos/old-root', 4 * GB, { mountPoint: '/usr/local' }),
  ],
};

/** The pools while a disk is added to the RAID-Z1 of tank. */
export const samplePoolsExpanding = {
  pools: [
    {
      ...tankPool,
      expansion: {
        state: 'scanning',
        vdevGuid: '9206321434375767675',
        missing: false,
        startedAt: '2026-09-26T09:12:00Z',
        endedAt: null,
        toReflowBytes: 10.1 * TB,
        reflowedBytes: 3.8 * TB,
        waitingForResilver: false,
      },
      vdevs: [
        {
          ...at(tankPool.vdevs, 0),
          children: [
            ...(tankPool.vdevs[0]?.children ?? []),
            leaf('ZA1B2D7H', '3411908277104522671'),
          ],
        },
      ],
    },
    backupPool,
  ] as Pool[],
  datasets: sampleDatasets,
};

export const sampleShares = [
  {
    id: 1,
    name: 'Family',
    description: 'Photos, documents and the scans of the paperwork',
    path: '/tank/family',
    pool: 'tank',
    dataset: 'tank/pithyx/shares/family',
    usedBytes: 1.4 * TB,
    availableBytes: 0.6 * TB,
    quotaBytes: 2 * TB,
    readUsers: 1,
    readWriteUsers: 2,
    restorePoints: true,
  },
  {
    id: 2,
    name: 'Media',
    description: 'Films and music for the living room',
    path: '/tank/media',
    pool: 'tank',
    dataset: 'tank/pithyx/shares/media',
    usedBytes: 4.9 * TB,
    availableBytes: 9.2 * TB,
    quotaBytes: null,
    readUsers: 3,
    readWriteUsers: 1,
    restorePoints: true,
  },
  {
    id: 3,
    name: 'Archive',
    description: '',
    path: '/backup/archive',
    pool: 'backup',
    dataset: 'backup/pithyx/shares/archive',
    usedBytes: 2.7 * TB,
    availableBytes: 1.2 * TB,
    quotaBytes: null,
    readUsers: 0,
    readWriteUsers: 0,
    restorePoints: false,
  },
];

export const sampleSystemOverview = {
  version: '0.1.0-dev',
  commit: '51962e2c0ffee5d1b4f0a1d6e2c9b7a8f3e4d5c6',
  operatingSystem: 'Debian GNU/Linux 13 (trixie)',
  kernel: '6.12.107+deb13-amd64',
  machine: 'Example Systems N305 Mini',
  cpuModel: 'Intel(R) Core(TM) i3-N305',
  cpuCores: 8,
  memoryTotalBytes: 32 * 1024 ** 3,
  hostname: 'nas',
  uptimeSeconds: 3 * 86_400 + 5 * 3600 + 17 * 60,
};

export const sampleUpdates: UpdateStatus = {
  installedVersion: '0.1.0',
  channel: 'stable',
  checkEnabled: true,
  lastCheckAt: '2026-10-09T03:12:00Z',
  nextCheckAt: '2026-10-10T03:12:00Z',
  checking: false,
  available: {
    version: '0.2.0',
    minimumVersion: '0.1.0',
    changelog:
      '### Added\n\n- Updates with a snapshot before and a health check after the restart.',
    size: 48_146_112,
    publishedAt: '2026-10-08T18:00:00Z',
    installable: true,
  },
  debian: { count: 7, security: 6, packages: [] },
  problem: null,
  supported: true,
  unsupportedReason: null,
  staged: null,
  runningJobId: null,
  history: [
    {
      id: 1,
      kind: 'update',
      source: 'online',
      fromVersion: '0.1.0-rc.2',
      toVersion: '0.1.0',
      snapshot: '20261001T030500Z',
      state: 'succeeded',
      errorCode: null,
      errorParams: null,
      startedAt: '2026-10-01T03:05:00Z',
      finishedAt: '2026-10-01T03:09:00Z',
    },
  ],
  snapshots: [
    {
      name: '20261001T030500Z',
      version: '0.1.0-rc.2',
      createdAt: '2026-10-01T03:05:00Z',
      kernel: '6.12.111+deb13-amd64',
      reason: 'update to 0.1.0',
    },
  ],
  source: {
    name: 'Pithyx',
    url: 'https://github.com/pithyx/pithyx/releases/download/channel-{channel}/channel.json',
    official: true,
    keys: [
      {
        keyId: 'pithyx-release-1',
        publicKey: 'X86BspTUgM8UdRY2ac31eIq2l7bRduf0leSj5x3+pZI=',
        channels: ['stable', 'beta'],
      },
    ],
  },
  sourceCandidate: null,
};

export const sampleNetwork = {
  config: {
    interfaces: [],
    global: { hostName: 'pithyx', dnsServers: [], searchDomains: [], defaultInterface: null },
  },
  links: [
    {
      name: 'enp1s0',
      kind: 'ethernet',
      macAddress: '02:00:00:12:34:56',
      up: true,
      carrier: true,
      speedMbps: 1000,
      duplex: 'full',
      mtu: 1500,
      addresses: ['192.168.1.20/24', 'fd00::20/64'],
      gateways: ['192.168.1.1'],
      dnsServers: ['192.168.1.1'],
      defaultRoute: true,
      master: null,
      vlanParent: null,
      vlanId: null,
      method: 'dhcp',
      configured: false,
      changeable: true,
    },
    {
      name: 'enp2s0',
      kind: 'ethernet',
      macAddress: '02:00:00:12:34:57',
      up: false,
      carrier: false,
      speedMbps: null,
      duplex: null,
      mtu: 1500,
      addresses: [],
      gateways: [],
      dnsServers: [],
      defaultRoute: false,
      master: null,
      vlanParent: null,
      vlanId: null,
      method: 'dhcp',
      configured: false,
      changeable: true,
    },
  ],
  hostName: 'pithyx',
  globalChangeable: true,
  pending: null,
  confirmSeconds: 60,
  connectionInterface: 'enp1s0',
};

export const sampleTime = {
  timeZone: 'Europe/Berlin',
  synchronized: true,
  ntpServers: ['0.debian.pool.ntp.org', '1.debian.pool.ntp.org'],
  timeZones: ['America/New_York', 'Asia/Tokyo', 'Europe/Berlin', 'Europe/Vienna', 'UTC'],
};

export const sampleShareAccess = [
  {
    userId: 1,
    username: 'alex',
    displayName: 'Alex Morgan',
    administrator: true,
    access: 'readWrite',
  },
  {
    userId: 2,
    username: 'sam',
    displayName: 'Sam Rivera',
    administrator: false,
    access: 'readWrite',
  },
  {
    userId: 3,
    username: 'robin',
    displayName: 'Robin Keller',
    administrator: false,
    access: 'read',
  },
  { userId: 4, username: 'kim', displayName: null, administrator: false, access: 'none' },
];

interface SharePlanRequest {
  action: string;
  name?: string | null;
  pool?: string | null;
  shareId?: number | null;
  data?: string | null;
}

function slug(name: string): string {
  return name
    .toLowerCase()
    .normalize('NFD')
    .replace(/[̀-ͯ]/g, '')
    .replace(/[^a-z0-9_.-]+/g, '-')
    .replace(/^-+|-+$/g, '');
}

/** The plan the daemon would answer for creating or deleting a shared folder. */
export function sampleSharePlan(request: SharePlanRequest): StoragePlanPreview {
  if (request.action === 'share.create') {
    const target = `${request.pool ?? 'tank'}/pithyx/shares/${slug(request.name ?? '')}`;
    return preview([`zfs create ${target}`], { datasets: [target] });
  }
  const share = sampleShares.find((entry) => entry.id === request.shareId);
  if (request.data !== 'destroy' || share?.dataset == null) {
    return preview([], { requiresConfirmName: true });
  }
  const snapshots =
    share.dataset === 'tank/pithyx/shares/family'
      ? sampleSnapshots.map((entry) => entry.fullName ?? '')
      : [];
  return preview([`zfs destroy${snapshots.length > 0 ? ' -r' : ''} ${share.dataset}`], {
    datasets: [share.dataset, ...snapshots],
    requiresConfirmName: true,
  });
}

export const sampleJob: Job = {
  id: 42,
  kind: 'storage.pool.create',
  state: 'running',
  createdAt: '2026-09-26T14:31:40Z',
  startedAt: '2026-09-26T14:31:41Z',
  finishedAt: null,
  progress: {
    itemsDone: 2,
    itemsTotal: 6,
    bytesDone: 0,
    bytesTotal: 0,
    currentItem: 'zfs create -o canmount=off -o mountpoint=/tank2 tank2/pithyx/shares',
  },
  parameters: null,
  result: null,
  errorCode: null,
  errorParams: null,
  requestId: '4bf92f3577b34da6a3ce929d0e0e4736',
};

/** Answers for internal methods the gallery does not know yet. */
export type ExtraAnswers = Record<string, (params: unknown) => unknown>;

export const sampleUsers: UserSummary[] = [
  {
    id: 1,
    username: 'ada',
    displayName: 'Ada Admin',
    isAdmin: true,
    uid: 20000,
    totpEnabled: true,
    createdAt: '2026-09-01T08:00:00Z',
    home: 'tank/pithyx/homes/ada',
    disabled: false,
    rights: ['right:admin', 'right:desktopLogin', 'right:sshLogin', 'right:power'],
    protocols: ['protocol:smb', 'protocol:sftp', 'protocol:webdav', 'protocol:ftp'],
    fileServicePassword: true,
    groupIds: [1],
  },
  {
    id: 2,
    username: 'ben',
    displayName: 'Ben Berger',
    isAdmin: false,
    uid: 20001,
    totpEnabled: false,
    createdAt: '2026-09-03T08:00:00Z',
    home: 'tank/pithyx/homes/ben',
    disabled: false,
    rights: ['right:desktopLogin'],
    protocols: ['protocol:smb', 'protocol:sftp'],
    fileServicePassword: true,
    groupIds: [2, 3],
  },
  {
    id: 3,
    username: 'carla.mueller-schmidt',
    displayName: 'Carla Müller-Schmidt',
    isAdmin: false,
    uid: 20002,
    totpEnabled: true,
    createdAt: '2026-09-05T08:00:00Z',
    home: 'tank/pithyx/homes/carla.mueller-schmidt',
    disabled: false,
    rights: ['right:desktopLogin'],
    protocols: ['protocol:smb'],
    fileServicePassword: false,
    groupIds: [3],
  },
  {
    id: 4,
    username: 'guest',
    displayName: 'Guest',
    isAdmin: false,
    uid: 20003,
    totpEnabled: false,
    createdAt: '2026-09-07T08:00:00Z',
    home: null,
    disabled: true,
    rights: [],
    protocols: [],
    fileServicePassword: false,
    groupIds: [],
  },
];

export const sampleGroups: GroupSummary[] = [
  {
    id: 1,
    name: 'administrators',
    description: 'Everyone who runs the box',
    builtin: true,
    memberIds: [1],
    createdAt: '2026-09-01T08:00:00Z',
  },
  {
    id: 2,
    name: 'family',
    description: 'Photos and documents of the family',
    builtin: false,
    memberIds: [2],
    createdAt: '2026-09-02T08:00:00Z',
  },
  {
    id: 3,
    name: 'media',
    description: '',
    builtin: false,
    memberIds: [2, 3],
    createdAt: '2026-09-02T09:00:00Z',
  },
];

export const sampleFileServices: FileServiceSettings = {
  smbEnabled: true,
  workgroup: 'WORKGROUP',
  sftpEnabled: true,
  smb: 'running',
  sftp: 'running',
  sshEnabled: false,
  webDavEnabled: false,
  ftpEnabled: true,
  ftpPlainAllowed: false,
  ftp: 'running',
  ftpPort: 21,
  ftpFingerprint: 'SHA256:4F:2A:9C:11:7B:E0:52:D8:6A:93:0C:FE:21:44:8B:3D',
  privateNetworksOnly: true,
  firewallActive: true,
};

export const sampleUps: UpsStatus = {
  settings: {
    mode: 'usb',
    host: '',
    port: 3493,
    name: 'ups',
    username: '',
    hasPassword: false,
    batteryLow: 20,
    runtimeLow: 5,
  },
  state: {
    connection: 'connected',
    power: 'online',
    lowBattery: false,
    charge: 100,
    runtime: 1860,
    load: 18,
    model: 'UPS 900VA',
    problem: null,
    onBatterySince: null,
    shutdownReason: null,
  },
  usbAvailable: true,
};

/** Answers the internal calls of the Storage and Settings apps from the sample, never from a daemon. */
function answer(method: string, params: unknown, extra: ExtraAnswers): unknown {
  const custom = extra[method];
  if (custom !== undefined) {
    return custom(params);
  }
  switch (method) {
    case 'internal.admin.storage.disks':
      return { disks: sampleDisks, environment: 'prod' };
    case 'internal.admin.storage.pools':
      return { pools: samplePools, datasets: sampleDatasets };
    case 'internal.admin.storage.plan':
      return samplePlan(params as StoragePlanRequest);
    case 'internal.admin.storage.run':
      return sampleJob;
    case 'internal.admin.storage.health':
      return sampleHealth;
    case 'internal.admin.storage.schedules':
      return sampleSchedules;
    case 'internal.admin.storage.snapshots.list':
      return {
        dataset: (params as { dataset: string }).dataset,
        snapshots:
          (params as { dataset: string }).dataset === 'tank/pithyx/shares/family'
            ? sampleSnapshots
            : [],
      };
    case 'internal.admin.storage.importScan':
      return sampleImportScan;
    case 'internal.admin.shares.list':
      return sampleShares;
    case 'internal.admin.shares.access':
      return sampleShareAccess;
    case 'internal.admin.shares.plan':
      return sampleSharePlan(params as SharePlanRequest);
    case 'internal.admin.system.overview':
      return sampleSystemOverview;
    case 'internal.admin.network.get':
      return sampleNetwork;
    case 'internal.admin.updates.get':
      return sampleUpdates;
    case 'internal.admin.system.time':
      return sampleTime;
    case 'internal.admin.users.list':
      return sampleUsers;
    case 'internal.admin.groups.list':
      return sampleGroups;
    case 'internal.admin.system.fileServices':
      return sampleFileServices;
    case 'internal.admin.ups.get':
      return sampleUps;
    case 'internal.admin.system.region':
      return { language: 'en', region: null };
    case 'internal.admin.storage.snapshots.schedules':
      return { ...sampleSnapshotSchedules, dataset: (params as { dataset: string }).dataset };
    default:
      return null;
  }
}

/** A stand-in for the SDK app of a core app: internal calls answered from the sample, no events. */
export function sampleApp(launch: unknown = null, extra: ExtraAnswers = {}): PithyxApp {
  const format = createFormatters(() => ({ locale: i18next.language, timeZone: 'Europe/Berlin' }));
  const app = {
    context: { launch: { args: launch }, locale: i18next.language, timeZone: 'Europe/Berlin' },
    format,
    call: (method: string, params: unknown) => Promise.resolve(answer(method, params, extra)),
    on: () => () => undefined,
  };
  return app as unknown as PithyxApp;
}
