-- The daemon's API for apps (A11, D61, D305). Apps keep small values per user in app_storage (storage.get, storage.set
-- of the SDK), in the backup part "Apps". The rows have no key on apps: an uninstall that keeps the app's data keeps
-- them, and one that deletes the data deletes them. A deleted user takes its values along.
CREATE TABLE app_storage (
    app_id TEXT NOT NULL CHECK (length(app_id) BETWEEN 5 AND 100),
    user_id INTEGER NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    key TEXT NOT NULL CHECK (length(key) BETWEEN 1 AND 128),
    -- JSON, at most 64 KB; an app keeps at most 1 MB of keys and values per user.
    value TEXT NOT NULL CHECK (json_valid(value) AND length(CAST(value AS BLOB)) <= 65536),
    updated_at TEXT NOT NULL,
    PRIMARY KEY (app_id, user_id, key)
) STRICT;

CREATE INDEX app_storage_user_id ON app_storage (user_id);

-- Whether debug entries of the app's log reach the Log Center (D305): an administrator switches it on while looking for
-- a fault. info is the default.
ALTER TABLE apps ADD COLUMN log_level TEXT NOT NULL DEFAULT 'info' CHECK (log_level IN ('info', 'debug'));
