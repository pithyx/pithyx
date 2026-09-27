-- Users, sessions, TOTP recovery codes and global settings for the first login flow.
-- Timestamps are ISO 8601 UTC text.

CREATE TABLE users (
    id INTEGER PRIMARY KEY,
    -- A valid Linux user name, stored in lower case; the daemon provisions Linux and Samba users from it.
    username TEXT NOT NULL UNIQUE CHECK (username = lower(username) AND length(username) BETWEEN 1 AND 32),
    display_name TEXT,
    password_hash TEXT NOT NULL,
    is_admin INTEGER NOT NULL DEFAULT 0 CHECK (is_admin IN (0, 1)),
    totp_secret TEXT,
    totp_enabled_at TEXT,
    totp_last_used_step INTEGER,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
) STRICT;

CREATE TABLE sessions (
    -- SHA-256 of the session token; the token itself is never stored.
    id BLOB PRIMARY KEY,
    user_id INTEGER NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    created_at TEXT NOT NULL,
    last_seen_at TEXT NOT NULL,
    expires_at TEXT NOT NULL,
    absolute_expires_at TEXT NOT NULL,
    user_agent TEXT
) STRICT;

CREATE INDEX sessions_user_id ON sessions (user_id);
CREATE INDEX sessions_expires_at ON sessions (expires_at);

CREATE TABLE recovery_codes (
    id INTEGER PRIMARY KEY,
    user_id INTEGER NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    code_hash BLOB NOT NULL,
    used_at TEXT
) STRICT;

CREATE INDEX recovery_codes_user_id ON recovery_codes (user_id);

CREATE TABLE settings (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
) STRICT;
