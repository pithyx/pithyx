-- Desktop layout memory (step 5.2). Layouts are per user and device and expire; window preferences and desktop
-- items are per user and shared across devices. Layouts never contain permissions.

CREATE TABLE desktop_layouts (
    user_id INTEGER NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    -- Random id from the long-lived pithyx_device cookie of a browser profile.
    device_id TEXT NOT NULL,
    -- Browser and operating system names from the User-Agent, for "Get desktop from ...".
    device_browser TEXT,
    device_os TEXT,
    schema_version INTEGER NOT NULL,
    -- JSON, validated and size-capped by the daemon.
    layout TEXT NOT NULL,
    window_count INTEGER NOT NULL,
    updated_at TEXT NOT NULL,
    expires_at TEXT NOT NULL,
    PRIMARY KEY (user_id, device_id)
) STRICT;

CREATE INDEX desktop_layouts_expires_at ON desktop_layouts (expires_at);

CREATE TABLE window_preferences (
    user_id INTEGER NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    app_id TEXT NOT NULL,
    -- The last free size of the app's windows, used for its next new window.
    width INTEGER NOT NULL CHECK (width > 0),
    height INTEGER NOT NULL CHECK (height > 0),
    updated_at TEXT NOT NULL,
    PRIMARY KEY (user_id, app_id)
) STRICT;

CREATE TABLE desktop_items (
    id INTEGER PRIMARY KEY,
    user_id INTEGER NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    kind TEXT NOT NULL CHECK (kind IN ('shortcut', 'file', 'note')),
    -- JSON: app id, path or note text.
    data TEXT NOT NULL,
    position INTEGER NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
) STRICT;

CREATE INDEX desktop_items_user_id ON desktop_items (user_id);
