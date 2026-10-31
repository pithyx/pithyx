/**
 * The ids of the apps that ship with the desktop, in the namespace `org.pithyx.*` that only system apps and the
 * official catalog may use (D302). The daemon has the same list in `Apps/CoreApps.cs`.
 */
export const coreAppIds = {
  files: 'org.pithyx.files',
  viewer: 'org.pithyx.viewer',
  // Storage was an app until D402; the id only names what was stored before (Log Center sources of that time).
  storage: 'org.pithyx.storage',
  settings: 'org.pithyx.settings',
  monitor: 'org.pithyx.monitor',
  logs: 'org.pithyx.logs',
  about: 'org.pithyx.about',
  preferences: 'org.pithyx.preferences',
  store: 'org.pithyx.store',
} as const;

const PREFIX = 'org.pithyx.';

// The short ids the core apps had before migration 0022 (files, viewer, ...); later core apps never had one.
const legacyIds: ReadonlySet<string> = new Set(
  Object.values(coreAppIds)
    .filter((id) => id !== coreAppIds.preferences)
    .map((id) => id.slice(PREFIX.length)),
);

/**
 * The id an app has now: a core app's short id from before migration 0022 becomes its `org.pithyx.*` id, any other
 * id stays. Log entries written before keep their short source (`app/files`); the Log Center names them after the app.
 */
export function currentAppId(id: string): string {
  return legacyIds.has(id) ? PREFIX + id : id;
}
