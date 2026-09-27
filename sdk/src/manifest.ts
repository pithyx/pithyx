/**
 * The app manifest (`vyxis.json` at the root of an app package). It tells the daemon and the desktop what the app is,
 * what it may do and how its window and backend behave. The manifest schema is a stable contract: a new optional field
 * is a minor change, anything else a major one.
 *
 * Not in manifest version 1: apps that talk to other apps, app code that runs without a window (backends excepted,
 * see {@link BackendManifest}), and desktop widgets.
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
 * Permissions an app declares. Nothing is granted by using an API: the install dialog lists every declared
 * permission, an administrator confirms them for the app, and each one can be revoked later (the app gets
 * `permissionRevoked`). Which users may use the app at all is a separate grant per user and group. The daemon checks
 * every permission on every call that reaches data or leaves the window; the desktop's own check only answers early.
 * An update that declares a new permission waits until an administrator confirms it; the old version keeps running.
 */
export type Permission =
  /** Read files the user opens with the app (`launch`, `openFile`) or picks in a desktop file dialog. */
  | 'files.read'
  /** Write those files, and save new files where the user chooses in a desktop save dialog. */
  | 'files.write'
  /**
   * Browse every share the user may access, with the user's own rights on each (the file manager). Paths are relative
   * to the share, never paths on the host.
   */
  | 'files.shares'
  /** Show notifications. */
  | 'notifications'
  /** Run a backend container (declared under `backend`). */
  | 'backend';

/**
 * Rights beyond the sandbox, each named on its own. They apply to the backend container only, never to the app's
 * window, which stays in its sandboxed frame. Only administrators can install an app that asks for one, after reading
 * what each right allows in plain language and confirming with their password (and TOTP when they use it). The app
 * then carries a permanent badge, every grant and revoke is written to the audit log, and each right can be revoked
 * on its own, which restarts the backend without it. An update that asks for a new one needs the same confirmation.
 *
 * Except for `host.full`, no right reaches the sessions, password hashes, TOTP secrets or recovery keys of any user.
 * The daemon enforces this: its database and secrets under `/var/lib/vyxis` and `/etc/vyxis` are never mapped into a
 * container, the Docker socket is only reachable through the daemon's filtering proxy, and the rights that would
 * reach them anyway (the system disk's block devices, `SYS_ADMIN`, `SYS_PTRACE`, `SYS_MODULE`, `SYS_RAWIO`,
 * `DAC_READ_SEARCH`, `BPF`) are never granted on their own.
 */
export type ElevatedPermission =
  /**
   * The Docker Engine API, through the daemon's filtering proxy: the backend can manage containers, but not start
   * one that is privileged, joins the host namespaces or mounts Vyxis' own state.
   */
  | 'docker.socket'
  /** The host's network namespace: the backend sees and binds every host interface and port. */
  | 'host.network'
  /** The host's process namespace: the backend sees every process on the host. */
  | 'host.pid'
  /** One device node, for example `device:/dev/dri/renderD128` for hardware video decoding. */
  | `device:${string}`
  /** One Linux capability, for example `capability:NET_ADMIN`. */
  | `capability:${string}`
  /**
   * A privileged container with the host's namespaces and devices: for system tools that manage the host itself. This
   * is technically root on the host, and the install warning says so plainly. The promise that an app never reaches
   * other users' secrets then holds only for what the SDK API hands out, not for the backend container.
   */
  | 'host.full';

/**
 * A place for files the app needs, declared by name. At install an administrator maps it to a folder in a real share
 * (and can change that later). The app only ever sees the name and paths inside it: the window reaches it through
 * the SDK file API as `{ storage: id }`, the backend finds it mounted at `/vyxis/storage/<id>`. Users reach it with
 * their own share rights on the mapped folder, never with more than `access`.
 */
export interface StorageRequest {
  /** Lowercase letters, digits and dashes, unique within the app. */
  id: string;
  /** What the administrator sees in the install dialog, for example "Photo library". */
  name: LocalizedText;
  description?: LocalizedText;
  access: 'read' | 'readWrite';
  /** The app cannot start until this storage is mapped. */
  required: boolean;
}

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

/**
 * Files the app can open. The desktop offers the app in "Open with" only for these types and sends it only such
 * files (`launch`, `openFile`).
 */
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
  /** Needs a `backend`. */
  elevated?: ElevatedPermission[];
  storage?: StorageRequest[];
  window: WindowManifest;
  backend?: BackendManifest;
  fileHandlers?: FileHandler[];
}
