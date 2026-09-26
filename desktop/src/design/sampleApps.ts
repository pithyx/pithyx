// Raw sample values shown inside the mockup windows. Names are user data; everything else is formatted with Intl
// and the i18n keys of the app namespaces.
export const filesApp = {
  path: ['alex', 'Photos 2026'],
  places: ['home', 'desktop', 'documents', 'Photos 2026'],
  shares: ['media', 'backup', 'family'],
  entries: [
    { name: 'Holiday Lisbon', kind: 'folder', items: 48, modified: '2026-09-21T10:12:00' },
    { name: 'Garden', kind: 'folder', items: 112, modified: '2026-09-14T18:40:00' },
    { name: 'IMG_2041.jpg', kind: 'image', bytes: 4_200_000, modified: '2026-09-25T09:03:00' },
    { name: 'IMG_2042.jpg', kind: 'image', bytes: 3_900_000, modified: '2026-09-25T09:04:00' },
    { name: 'IMG_2043.heic', kind: 'image', bytes: 2_800_000, modified: '2026-09-25T09:05:00' },
    {
      name: 'drone-flight.mp4',
      kind: 'video',
      bytes: 812_000_000,
      modified: '2026-09-19T16:22:00',
    },
    { name: 'album-cover.png', kind: 'image', bytes: 1_100_000, modified: '2026-09-02T12:00:00' },
  ],
  totalBytes: 821_000_000,
} as const;

export const storageApp = {
  pool: 'tank',
  layout: 'RAID-Z1',
  usedBytes: 7_900_000_000_000,
  totalBytes: 21_800_000_000_000,
  disks: [
    { number: 1, model: 'WDC WD80EFZZ', temperature: 38, state: 'active' },
    { number: 2, model: 'WDC WD80EFZZ', temperature: 39, state: 'active' },
    { number: 3, model: 'WDC WD80EFZZ', temperature: 52, state: 'warm' },
    { number: 4, model: 'Seagate ST8000VN004', temperature: undefined, state: 'standby' },
  ],
} as const;

export const logLines = [
  ['14:31:58', 'info', 'storage', 'udev: disk sdd changed state to standby'],
  ['14:31:02', 'info', 'auth', 'session created for alex from 192.168.1.24'],
  ['14:30:41', 'warn', 'storage', 'sdc temperature 52 C above threshold 50 C'],
  ['14:28:13', 'info', 'jobs', 'scrub tank finished, 0 errors, 3h12m'],
  ['14:12:09', 'error', 'apps', 'immich backend exited with code 137, restarting'],
  ['14:12:11', 'info', 'apps', 'immich backend healthy'],
  ['13:58:40', 'info', 'shares', 'smb: media mounted by 192.168.1.31'],
] as const;

export const accentPresets = ['petrol', 'ocean', 'indigo', 'plum', 'moss', 'graphite'] as const;
