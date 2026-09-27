-- Shares (step 6, before M3 brings pools, datasets and SMB): a name and a folder on the host, plus per-user access.
-- No row in share_access means no access; administrators can use every share. Groups join with M3.

CREATE TABLE shares (
    id INTEGER PRIMARY KEY,
    name TEXT NOT NULL UNIQUE COLLATE NOCASE CHECK (length(name) BETWEEN 1 AND 64),
    -- Absolute, canonical path of the share root on the host. Never sent to users who are not administrators.
    path TEXT NOT NULL UNIQUE,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
) STRICT;

CREATE TABLE share_access (
    share_id INTEGER NOT NULL REFERENCES shares (id) ON DELETE CASCADE,
    user_id INTEGER NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    level TEXT NOT NULL CHECK (level IN ('read', 'readWrite')),
    PRIMARY KEY (share_id, user_id)
) STRICT;

CREATE INDEX share_access_user_id ON share_access (user_id);
