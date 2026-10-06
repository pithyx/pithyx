-- The app registry (A10, D300, D301): third-party apps installed from a package. Only the registry lives here, in
-- the backup part "Apps"; the frontends, volumes and Docker's data live on the app pool (setting apps.pool, chosen at
-- the first install), one dataset per app. Who may use an app is a grant on the resource app:<id> (the built-in
-- group "users" for everybody); an app without a grant is for administrators only.

CREATE TABLE apps (
    -- An app id of D302; org.pithyx.* comes only from the system apps and the official catalog, never a sideload.
    id TEXT PRIMARY KEY CHECK (length(id) BETWEEN 5 AND 100 AND id GLOB '[a-z]*.*.*' AND id NOT GLOB '*[^a-z0-9.-]*'),
    -- The installed version (SemVer without build metadata) and its manifest as the package had it.
    version TEXT NOT NULL CHECK (length(version) BETWEEN 5 AND 64 AND version NOT GLOB '*[^0-9A-Za-z.-]*'),
    manifest TEXT NOT NULL CHECK (json_valid(manifest)),
    -- Where it came from: a package file an administrator uploaded, or later a catalog under the same id (D308).
    source TEXT NOT NULL CHECK (source IN ('sideload', 'catalog')),
    -- The SVG from the package, served at /api/apps/{id}/icon.
    icon BLOB NOT NULL CHECK (length(icon) BETWEEN 1 AND 65536),
    -- SHA-256 of the package file, lowercase hex.
    package_sha256 TEXT NOT NULL CHECK (length(package_sha256) = 64),
    -- The app's own uid (40000 to 49999), kept for the same id across an uninstall that keeps the data.
    uid INTEGER NOT NULL UNIQUE REFERENCES id_allocations (id),
    -- A disabled app keeps its data and grants, but nobody can open it and its frontend is not served.
    enabled INTEGER NOT NULL DEFAULT 1 CHECK (enabled IN (0, 1)),
    installed_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
) STRICT;

-- The permissions of the manifest (files.read, notifications, ...), each granted or revoked by an administrator. An
-- update adds the new ones granted and drops those the manifest no longer asks for; a revoked one stays revoked.
CREATE TABLE app_permissions (
    app_id TEXT NOT NULL REFERENCES apps (id) ON DELETE CASCADE,
    permission TEXT NOT NULL CHECK (length(permission) BETWEEN 1 AND 200),
    granted INTEGER NOT NULL DEFAULT 1 CHECK (granted IN (0, 1)),
    PRIMARY KEY (app_id, permission)
) STRICT;

-- The folders an administrator assigned to the storage requests of an app's manifest (A12, A14): a folder inside a
-- shared folder, read-only or read-write. A request without a row is not mapped.
CREATE TABLE storage_mappings (
    app_id TEXT NOT NULL REFERENCES apps (id) ON DELETE CASCADE,
    storage_id TEXT NOT NULL CHECK (length(storage_id) BETWEEN 1 AND 64),
    share_id INTEGER NOT NULL REFERENCES shares (id) ON DELETE CASCADE,
    -- Relative to the shared folder's root, without a leading slash; empty for the root itself.
    path TEXT NOT NULL DEFAULT '' CHECK (path NOT GLOB '/*' AND length(path) <= 1024),
    access TEXT NOT NULL CHECK (access IN ('read', 'readWrite')),
    PRIMARY KEY (app_id, storage_id)
) STRICT;

CREATE INDEX storage_mappings_share_id ON storage_mappings (share_id);

CREATE TRIGGER apps_grants_delete AFTER DELETE ON apps
BEGIN
    DELETE FROM grants WHERE resource = 'app:' || old.id;
END;

-- The SDK demo was a hand-wired app (org.pithyx.demo since 0022); it is the E2E probe now, installed from its package
-- like any third-party app, so its grants go.
DELETE FROM grants WHERE resource = 'app:org.pithyx.demo';
