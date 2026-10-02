/**
 * What a dataset is to Pithyx, by its name relative to the pool's layout. The daemon's DatasetPlanner decides; this
 * copy only hides actions it would refuse.
 */
export type DatasetRole = 'root' | 'structure' | 'managed' | 'share' | 'volume' | 'other';

export function datasetRole(pool: string, name: string): DatasetRole {
  if (name === pool) {
    return 'root';
  }
  const layout = `${pool}/pithyx`;
  if (name === layout) {
    return 'structure';
  }
  if (!name.startsWith(`${layout}/`)) {
    return 'other';
  }
  const parts = name.slice(layout.length + 1).split('/');
  if (parts.length === 1) {
    return 'structure';
  }
  return parts[0] === 'shares' ? 'share' : parts[0] === 'volumes' ? 'volume' : 'managed';
}

/**
 * Datasets the Storage app never shows: the Pithyx layout (`pithyx` and its `apps`, `system`, `homes`, `shares`,
 * `volumes` and `cache`) and what the daemon manages below it (homes, app data, the thumbnail cache). The pool root,
 * shared folders, volumes and datasets outside the layout stay visible.
 */
export function isHiddenDataset(pool: string, name: string): boolean {
  const role = datasetRole(pool, name);
  return role === 'structure' || role === 'managed';
}

/** Renaming and deleting: shares, volumes and datasets outside the layout. */
export function canRenameOrDelete(role: DatasetRole): boolean {
  return role === 'share' || role === 'volume' || role === 'other';
}

/** Volumes, the pool root and the layout datasets take compression only. */
export function compressionOnly(role: DatasetRole, type: string): boolean {
  return type === 'volume' || role === 'root' || role === 'structure';
}

/** The compressions the daemon sets, in the order the select shows them. */
export const COMPRESSIONS = [
  'lz4',
  'zstd',
  ...Array.from({ length: 19 }, (_, index) => `zstd-${String(index + 1)}`),
  'gzip',
  ...Array.from({ length: 9 }, (_, index) => `gzip-${String(index + 1)}`),
  'zle',
  'off',
] as const;

/** Record sizes from 4 KiB to 1 MiB. */
export const RECORD_SIZES = Array.from({ length: 9 }, (_, index) => 4096 * 2 ** index);

/** Size units of the size fields, decimal like the sizes in the tables. */
export const SIZE_UNITS = { mb: 1e6, gb: 1e9, tb: 1e12 } as const;
export type SizeUnit = keyof typeof SIZE_UNITS;

/**
 * Bytes from a typed number and unit; a comma counts as the decimal separator. Empty text is null; anything that is
 * not a number of zero or more is undefined.
 */
export function parseSize(text: string, unit: SizeUnit): number | null | undefined {
  const trimmed = text.trim();
  if (trimmed === '') {
    return null;
  }
  if (!/^\d+([.,]\d+)?$/.test(trimmed)) {
    return undefined;
  }
  return Math.round(Number(trimmed.replace(',', '.')) * SIZE_UNITS[unit]);
}

/** A byte count as text for a size field: the largest unit it fills, at most three decimals. */
export function sizeText(bytes: number | null, locale: string): { text: string; unit: SizeUnit } {
  if (bytes === null) {
    return { text: '', unit: 'gb' };
  }
  const unit: SizeUnit = bytes >= SIZE_UNITS.tb ? 'tb' : bytes >= SIZE_UNITS.gb ? 'gb' : 'mb';
  const text = new Intl.NumberFormat(locale, {
    maximumFractionDigits: 3,
    useGrouping: false,
  }).format(bytes / SIZE_UNITS[unit]);
  return { text, unit };
}
