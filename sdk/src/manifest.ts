/**
 * The app manifest (`vyxis.json` at the root of an app package). It tells the daemon and the desktop what the app is,
 * what it may do and how its window and backend behave. The manifest schema is a stable contract: a new optional field
 * is a minor change, anything else a major one.
 *
 * @packageDocumentation
 */

/** Lowercase letters, digits and dashes, starting with a letter, at most 64 characters. Unique per box. */
export type AppId = string;

/** A text in several languages, keyed by BCP 47 language tag. `en` is required and used when nothing else matches. */
export type LocalizedText = { en: string } & Partial<Record<string, string>>;

/** A size in CSS pixels. */
export interface WindowSize {
  width: number;
  height: number;
}

/**
 * Permissions an app asks for. The install dialog lists them; an administrator grants them per user or group, and
 * the desktop checks them on every call. Revoking one ends the affected windows (see the `permissionRevoked` event).
 */
export type Permission =
  /** Read files the user opens with the app or picks in a file dialog. */
  | 'files.read'
  /** Write those files. */
  | 'files.write'
  /** Show notifications. */
  | 'notifications'
  /** Read the list of shares the user can access. */
  | 'shares.read'
  /** Run a backend container (declared under `backend`). */
  | 'backend';

/** Permissions that let an app escape its sandbox. The install dialog shows them separately and in red. */
export type ElevatedPermission = 'docker.socket' | 'host.network' | 'privileged';

export interface WindowManifest {
  /**
   * `single`: opening the app again focuses the user's existing window on this device and passes the launch arguments
   * to it (`launch` event). `multi`: every launch opens a new window.
   */
  instances: 'single' | 'multi';
  /** Size of a new window when the user has no remembered size for the app. */
  defaultSize: WindowSize;
  /** The window never gets smaller than this on screens that can fit it. */
  minSize: WindowSize;
}

/** A recurring backend job. The backend runs for it even without an open window. */
export interface BackendSchedule {
  /** Stable name, shown in the jobs list. */
  id: string;
  /** Five-field cron expression in the box's time zone. */
  cron: string;
  /** Path under the backend's base URL that the daemon calls (POST) when the schedule fires. */
  path: string;
}

export interface BackendManifest {
  /** Container image, pinned by digest. */
  image: string;
  /** Port the backend listens on inside its internal network. It never gets a host port. */
  port: number;
  /**
   * `always`: runs while the app is installed. `onDemand`: runs while at least one window of the app is open for any
   * user, plus `idleTimeout`, or while a keep-alive lease or a schedule holds it.
   */
  lifecycle: 'always' | 'onDemand';
  /** Seconds an `onDemand` backend keeps running after its last window closed. Default 600. */
  idleTimeout?: number;
  /** Limits for the container. */
  resources?: {
    /** Memory limit in MiB. */
    memory?: number;
    /** CPU limit in cores, for example 0.5. */
    cpus?: number;
  };
  schedules?: BackendSchedule[];
}

/** Files the app can open. The desktop offers the app in "Open with" and sends `openFile` to it. */
export interface FileHandler {
  /** Lowercase extensions without the dot. */
  extensions: string[];
  /** MIME types, for example `image/png`. */
  mimeTypes?: string[];
  /** Whether the app is the default for these files when it is the only handler. */
  isDefault?: boolean;
}

export interface AppManifest {
  /** The manifest schema this file follows. */
  manifestVersion: 1;
  id: AppId;
  /** SemVer of the app. */
  version: string;
  name: LocalizedText;
  description?: LocalizedText;
  /** Path of an SVG icon inside the package. */
  icon: string;
  /** Path of the HTML page the desktop loads in the sandboxed window, inside the package. */
  entry: string;
  /** SemVer range of the SDK the app was built with, for example `^1.0.0`. */
  sdk: string;
  permissions: Permission[];
  elevated?: ElevatedPermission[];
  window: WindowManifest;
  backend?: BackendManifest;
  fileHandlers?: FileHandler[];
}
