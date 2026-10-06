import type { components } from '@pithyx/api-client';
import { coreAppIds, probeAppId } from './ids';
import { findApp } from './registry';

export type SessionPermissions = components['schemas']['SessionPermissions'];

/** The apps given by a grant, as the daemon's permission catalog lists them; the others belong to the administrator right or to everybody. */
export const grantedApps: readonly string[] = [coreAppIds.files, coreAppIds.viewer, probeAppId];

let current: SessionPermissions | undefined;

/** The signed-in user's rights, kept here for opening and restoring windows outside React. */
export function setAppPermissions(permissions: SessionPermissions | undefined): void {
  current = permissions;
}

/**
 * Whether the user may open an app: administration apps for administrators, given apps by their grant, the rest for
 * everybody. Without known rights only the apps for everybody.
 */
export function isAppAllowed(
  id: string,
  permissions: SessionPermissions | undefined = current,
): boolean {
  const app = findApp(id);
  if (app === undefined) {
    return false;
  }
  // The probe is wired in by id until the topic apps lists installed apps (A12): it shows only while it is installed
  // and granted, for administrators too.
  if (id === probeAppId) {
    return permissions?.apps.includes(id) ?? false;
  }
  const admin = permissions?.admin === true;
  if (app.adminOnly === true) {
    return admin;
  }
  return !grantedApps.includes(id) || admin || (permissions?.apps.includes(id) ?? false);
}
