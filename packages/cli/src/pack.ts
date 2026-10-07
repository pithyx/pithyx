import { statSync } from 'node:fs';
import type { AppManifest } from '@pithyx/sdk';
import {
  CHECKSUMS_FILE,
  checksums,
  entryBytes,
  packageOrder,
  type PackageEntry,
  type Project,
} from './project';
import { writeTarGz, type TarFile } from './tar';

/** The file name of an app's package (D301). */
export function packageName(manifest: Pick<AppManifest, 'id' | 'version'>): string {
  return `${manifest.id}-${manifest.version}.pithyxapp`;
}

/** The entries of the package with its `CHECKSUMS`, in package order. */
export function packageEntries(entries: readonly PackageEntry[]): PackageEntry[] {
  const files = entries.filter((entry) => entry.path !== CHECKSUMS_FILE);
  return [...files, { path: CHECKSUMS_FILE, source: checksums(files) }].sort((a, b) =>
    packageOrder(a.path, b.path),
  );
}

function tarFile(entry: PackageEntry): TarFile {
  return {
    path: entry.path,
    size: typeof entry.source === 'string' ? statSync(entry.source).size : entry.source.length,
    read: () => entryBytes(entry),
  };
}

/**
 * Writes the package of a checked project to `target`: a gzip-compressed ustar archive of its files plus
 * `CHECKSUMS`, with the manifest and the icon first, every other path sorted, fixed modes, owners and times and no
 * timestamp in the gzip header, so the same files give the same bytes (D315). `extra` adds generated files such as
 * offline images.
 */
export async function pack(
  project: Pick<Project, 'entries'>,
  target: string,
  extra: readonly PackageEntry[] = [],
): Promise<void> {
  await writeTarGz(packageEntries([...project.entries, ...extra]).map(tarFile), target);
}
