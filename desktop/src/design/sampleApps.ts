// Sample content shown inside the mockup windows. English only, like sampleData.ts.
export const filesApp = {
  back: 'Back',
  forward: 'Forward',
  pathLabel: 'Path',
  listLabel: 'Folder contents',
  path: ['alex', 'Photos 2026'],
  search: 'Search in Photos 2026',
  places: ['Home', 'Desktop', 'Documents', 'Photos 2026'],
  shares: ['media', 'backup', 'family'],
  placesTitle: 'Places',
  sharesTitle: 'Shares',
  columns: { name: 'Name', size: 'Size', modified: 'Modified' },
  entries: [
    { name: 'Holiday Lisbon', kind: 'folder', size: '48 items', modified: 'Sep 21' },
    { name: 'Garden', kind: 'folder', size: '112 items', modified: 'Sep 14' },
    { name: 'IMG_2041.jpg', kind: 'image', size: '4.2 MB', modified: 'Sep 25' },
    { name: 'IMG_2042.jpg', kind: 'image', size: '3.9 MB', modified: 'Sep 25' },
    { name: 'IMG_2043.heic', kind: 'image', size: '2.8 MB', modified: 'Sep 25' },
    { name: 'drone-flight.mp4', kind: 'video', size: '812 MB', modified: 'Sep 19' },
    { name: 'album-cover.png', kind: 'image', size: '1.1 MB', modified: 'Sep 2' },
  ],
  status: '7 items, 821 MB',
} as const;

export const storageApp = {
  pool: 'tank',
  layout: 'RAID-Z1, 4 disks',
  health: 'Healthy',
  used: 'Used 7.9 TB of 21.8 TB',
  usedPercent: 36,
  disksTitle: 'Disks',
  disks: [
    { name: 'Disk 1', model: 'WDC WD80EFZZ', temp: '38 °C', state: 'Active' },
    { name: 'Disk 2', model: 'WDC WD80EFZZ', temp: '39 °C', state: 'Active' },
    { name: 'Disk 3', model: 'WDC WD80EFZZ', temp: '52 °C', state: 'Warm' },
    { name: 'Disk 4', model: 'Seagate ST8000VN004', temp: '', state: 'Sleeping' },
  ],
  sleepingHint: 'Sleeping disks are not woken for status queries.',
} as const;

export const logsApp = {
  filter: 'Filter logs',
  listLabel: 'Log entries',
  lines: [
    ['14:31:58', 'info', 'storage', 'udev: disk sdd changed state to standby'],
    ['14:31:02', 'info', 'auth', 'session created for alex from 192.168.1.24'],
    ['14:30:41', 'warn', 'storage', 'sdc temperature 52 C above threshold 50 C'],
    ['14:28:13', 'info', 'jobs', 'scrub tank finished, 0 errors, 3h12m'],
    ['14:12:09', 'error', 'apps', 'immich backend exited with code 137, restarting'],
    ['14:12:11', 'info', 'apps', 'immich backend healthy'],
    ['13:58:40', 'info', 'shares', 'smb: media mounted by 192.168.1.31'],
  ],
} as const;

export const settingsApp = {
  navLabel: 'Settings sections',
  nav: ['Personalization', 'Users and groups', 'Network', 'Storage', 'Updates', 'About'],
  title: 'Personalization',
  theme: 'Theme',
  themes: ['System', 'Light', 'Dark'],
  accent: 'Accent color',
  accents: ['petrol', 'ocean', 'indigo', 'plum', 'rose', 'moss', 'graphite'],
  accentNames: {
    petrol: 'Petrol',
    ocean: 'Ocean',
    indigo: 'Indigo',
    plum: 'Plum',
    rose: 'Rose',
    moss: 'Moss',
    graphite: 'Graphite',
  },
  taskbar: 'Taskbar position',
  positions: ['Bottom', 'Top'],
  wallpaper: 'Wallpaper',
  wallpaperHint: 'Pick a picture from your files or use the default.',
} as const;
