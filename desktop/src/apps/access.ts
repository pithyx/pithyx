import type { components } from '@pithyx/api-client';
import { findApp } from './registry';

export type SessionPermissions = components['schemas']['SessionPermissions'];

let current: SessionPermissions | undefined;

/** The signed-in user's rights, kept here for opening and restoring windows outside React. */
export function setAppPermissions(permissions: SessionPermissions | undefined): void {
  current = permissions;
}

/**
 * Whether the user may open an app: administration apps for administrators, given core apps by their grant, the
 * installed apps the topic `apps` lists for the user (the daemon checked the grant), the rest for everybody. Without
 * known rights only the apps for everybody.
 */
export function isAppAllowed(
  id: string,
  permissions: SessionPermissions | undefined = current,
): boolean {
  const app = findApp(id);
  if (app === undefined) {
    return false;
  }
  if (app.thirdParty === true) {
    return permissions !== undefined;
  }
  const admin = permissions?.admin === true;
  if (app.adminOnly === true) {
    return admin;
  }
  return app.granted !== true || admin || (permissions?.apps.includes(id) ?? false);
}
