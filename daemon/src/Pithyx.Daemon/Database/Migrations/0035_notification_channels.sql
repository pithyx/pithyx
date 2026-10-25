-- Notifications beyond the desktop (D379): email and webhook channels. A channel without a user is one of the box,
-- set up by administrators, and gets the system's own alerts; a user's channel gets what reaches that user. The SMTP
-- server is the setting notifications.smtp.
CREATE TABLE notification_channels (
    id INTEGER PRIMARY KEY,
    user_id INTEGER REFERENCES users (id) ON DELETE CASCADE,
    name TEXT NOT NULL,
    kind TEXT NOT NULL CHECK (kind IN ('email', 'webhook')),
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    -- JSON: the recipients of an email channel, or the URL, format and headers of a webhook (with their secrets).
    config TEXT NOT NULL,
    -- JSON array of storage, updates, backups, security, apps, system.
    categories TEXT NOT NULL,
    min_level TEXT NOT NULL CHECK (min_level IN ('info', 'warning', 'error')),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    last_sent_at TEXT,
    -- Why the last delivery failed, cleared by the next one that works.
    error_code TEXT,
    error_params TEXT
) STRICT;

CREATE INDEX notification_channels_user ON notification_channels (user_id);

-- The language a user's desktop last used, for what the box writes to them itself (D379). Without a row the system
-- language applies.
CREATE TABLE user_languages (
    user_id INTEGER PRIMARY KEY REFERENCES users (id) ON DELETE CASCADE,
    language TEXT NOT NULL,
    updated_at TEXT NOT NULL
) STRICT;
