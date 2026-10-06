/**
 * The app manifest (`pithyx.json` at the root of an app package). It tells the daemon and the desktop what the app is,
 * what it may do and how its window and backend behave. The source of the contract is the JSON Schema
 * `@pithyx/sdk/schema/manifest.v1.schema.json`; these types are generated from it. Point `$schema` in `pithyx.json` at
 * `./node_modules/@pithyx/sdk/schema/manifest.v1.schema.json` for completion and checks in the editor, and run
 * `pithyx validate` for the rules a schema cannot express.
 *
 * The manifest schema is a stable contract: a new optional field is a minor change, anything else a major one.
 *
 * Not in manifest version 1: apps that talk to other apps, app code that runs without a window (backends excepted,
 * see {@link BackendManifest}), web apps with their own port, and desktop widgets.
 *
 * @packageDocumentation
 */

export type {
  AppAuthor,
  AppId,
  AppManifest,
  BackendEndpoint,
  BackendHealth,
  BackendManifest,
  BackendSchedule,
  ElevatedPermission,
  FileHandler,
  LocalizedName,
  LocalizedText,
  Permission,
  StorageRequest,
  WindowManifest,
  WindowSize,
} from './manifest.generated';
