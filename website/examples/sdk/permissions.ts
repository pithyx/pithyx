// A team board that offers what its permissions allow and follows when an administrator changes them.
// pithyx.json declares "notifications" and "directory.read" in "permissions", each with its "reason".
import { PithyxError, type DirectoryUser, type PithyxApp } from '@pithyx/sdk';

interface Features {
  notify: boolean;
  pickPeople: boolean;
}

/** What the app may do now; context.permissions is always the current, complete list. */
export function features(app: PithyxApp): Features {
  const has = (permission: string) => app.context.permissions.includes(permission);
  return { notify: has('notifications'), pickPeople: has('directory.read') };
}

/** Everyone to assign a card to, or only the signed-in user without `directory.read`. */
export async function people(
  app: PithyxApp,
): Promise<Pick<DirectoryUser, 'id' | 'name' | 'displayName'>[]> {
  const { user } = app.context;
  if (!features(app).pickPeople) {
    return [{ id: user.id, name: user.name, displayName: user.displayName }];
  }
  try {
    return await app.directory.users();
  } catch (error) {
    // Revoked between the check and the call: the SDK or the daemon refuses with the missing permission.
    if (error instanceof PithyxError && error.code === 'app.permission.denied') {
      return [{ id: user.id, name: user.name, displayName: user.displayName }];
    }
    throw error;
  }
}

export async function remind(app: PithyxApp, title: string): Promise<boolean> {
  if (!features(app).notify) {
    return false;
  }
  try {
    await app.notify({ title, level: 'info', tag: 'board-reminder' });
    return true;
  } catch (error) {
    if (error instanceof PithyxError && error.code === 'app.permission.denied') {
      return false;
    }
    throw error;
  }
}

/** Renders once and again after every change; returns the function that stops following. */
export function follow(app: PithyxApp, render: (features: Features) => void): () => void {
  render(features(app));
  return app.on('permissionsChange', () => {
    // The SDK has replaced context.permissions and dropped its backend token before handlers run.
    render(features(app));
  });
}
