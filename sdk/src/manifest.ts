/**
 * The app manifest (`pithyx.json` at the root of an app package). It tells the daemon and the desktop what the app is,
 * what it may do and how its window and backend behave. The source of the contract is the JSON Schema
 * `@pithyx/sdk/schema/manifest.v2.schema.json`; these types are generated from it. Point `$schema` in `pithyx.json` at
 * `./node_modules/@pithyx/sdk/schema/manifest.v2.schema.json` for completion and checks in the editor, and run
 * `pithyx validate` for the rules a schema cannot express.
 *
 * The manifest schema is a stable contract: a new optional field is a minor change, anything else a major one.
 *
 * Not in manifest version 2: apps that talk to other apps, app code that runs without a window (backends excepted,
 * see {@link BackendManifest}), web apps with their own port, and desktop widgets.
 *
 * @packageDocumentation
 */

import type { Permissions } from './manifest.generated';

export type {
  AppAuthor,
  AppId,
  AppManifest,
  BackendEndpoint,
  BackendHealth,
  BackendManifest,
  BackendSchedule,
  FileAction,
  FileHandler,
  LocalizedName,
  LocalizedText,
  Permissions,
  Screenshot,
  StorageRequest,
  WindowLoading,
  WindowManifest,
  WindowSize,
} from './manifest.generated';

/**
 * The name of a permission an app declares in `permissions`, the same list as the schema's `Permission`. Each one
 * reaches the app's token and the SDK's permission list, and an administrator can revoke it.
 */
export type Permission =
  | 'files.read'
  | 'files.write'
  | 'files.shares'
  | 'notifications'
  | 'notifications.external'
  | 'directory.read'
  | 'network.internet'
  | 'network.lan';

/**
 * The name of a right beyond the sandbox for the backend, declared in `permissions` like a permission (the schema's
 * `ElevatedPermission`). It never reaches the app's token; the box sorts it into a risk tier (D388).
 */
export type ElevatedPermission =
  | 'docker.socket'
  | 'docker.socket.raw'
  | 'host.network'
  | 'host.pid'
  | 'host.full'
  | `device:/dev/${string}`
  | `capability:${string}`;

/** What the app says about one entry of `permissions`: its reason, and `optional` or `hosts` where they apply. */
export type PermissionGrant = Permissions[string];
