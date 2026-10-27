---
title: 'Permissions and permissionsChange'
description: 'Declare permissions, check context.permissions, handle app.permission.denied and follow permissionsChange when an administrator changes them.'
status: ready
audience: developer
sidebar:
  order: 4
---

An app gets exactly the permissions it declares in `pithyx.json` and an administrator confirms. The administrator can revoke each one later and grant it again; the window then gets `permissionsChange` with the complete new list. Using an API grants nothing: a call without the permission fails with `app.permission.denied`. Build the interface from `app.context.permissions` and keep it current with the event.

## The permissions

| Permission               | Lets the app                                                                                                             |
| ------------------------ | ------------------------------------------------------------------------------------------------------------------------ |
| `files.read`             | read files in shared folders (with `files.shares`) and files the user hands to it                                        |
| `files.write`            | write those files                                                                                                        |
| `files.shares`           | list the shared folders the user can use and browse them with the user's rights                                          |
| `notifications`          | show notifications with `app.notify`                                                                                     |
| `notifications.external` | lets them also reach the users' email and webhook channels, together with `notifications`                                |
| `directory.read`         | list all users and groups with `app.directory.users()` and `groups()`                                                    |
| `network.internet`       | open connections to the internet from the window (`connect-src https:`) and from the backend                             |
| `network.lan`            | open connections from the backend to other devices in the local network; the window and the box itself stay out of reach |

Everything else needs no permission: the context, window calls, `app.storage`, `app.session`, logs, the backend and storage requests the administrator mapped. Which users may open the app at all is a separate grant per user and group, not a permission of the app.

Asking for `directory.read` makes the install dialog suggest the app for administrators only, as root containers and special rights do; ask for it only when the app really needs everybody's names.

## Example

The manifest declares `notifications` and `directory.read`, each with the `reason` the install dialog shows (see [Permissions](/docs/developers/apps/manifest/#permissions)):

```json
"permissions": {
  "notifications": { "reason": { "en": "Tells you when a card is assigned to you." } },
  "directory.read": { "reason": { "en": "Lets you assign cards to other users." } }
}
```

The board works without either and offers more with them:

<!-- example: sdk/permissions.ts -->

```ts
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
```

## How changes arrive

1. At install the dialog lists every declared permission and the administrator confirms them. An update that declares a new permission waits until an administrator confirms it; a revoked permission stays revoked across updates.
2. When an administrator grants or revokes one, every open window of the app gets `permissionsChange` with the full list. Before your handlers run, the SDK replaces `context.permissions` and drops its backend token, so the next backend call gets a fresh one.
3. A call that needs a revoked permission fails with [`app.permission.denied`](/docs/reference/errors/#app.permission.denied); `params.permission` names it. The SDK refuses `directory.*` at once, the daemon checks every call again.
4. When the user loses access to the app itself (the grant is revoked, or the app is disabled or removed), its windows close and the backend's sockets of that user are closed.

## Edge cases

- **Race.** A permission can go between your check and your call. Handle `app.permission.denied` on every call that needs one, as the example does.
- **`network.internet`** is part of the window's Content-Security-Policy, which the browser fixes when the page loads. A change applies to the window after a reload. A backend gets or loses its internet network with a restart. **`network.lan`** works the same way for the backend.
- **Admins** have every app, but an app still holds only the permissions confirmed for it.
- **Shared folders** need `files.shares` for the list and `files.read` or `files.write` for content; the user's own access to each shared folder applies on top. See [Files and app storage](/docs/sdk/guides/files-and-storage/).
- **Mock desktop.** The panel of `pithyx dev` lists the manifest's permissions as switches; each switch sends `permissionsChange`. The mock refuses `notifications`, `directory.*` and its file calls without the permission, like the box.

## Error codes

[`app.permission.denied`](/docs/reference/errors/#app.permission.denied), [`app.token.invalid`](/docs/reference/errors/#app.token.invalid), [`app.app.not_found`](/docs/reference/errors/#app.app.not_found). An administrator who grants a permission the manifest does not declare gets [`app.permission.not_requested`](/docs/reference/errors/#app.permission.not_requested); the app never sees it.
