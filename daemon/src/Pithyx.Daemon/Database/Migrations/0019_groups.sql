-- Groups (step 10, D289). A group's id is its Linux gid from the group range of D103 (30000 to 39999), allocated in
-- id_allocations and never given out again, like a user's uid. The built-in group "users" is the system group of
-- that name (gid 100), the primary group of every Pithyx account: every user is a member, so it has no rows in
-- group_members, and it can neither be renamed nor deleted.

CREATE TABLE groups (
    id INTEGER PRIMARY KEY,
    -- A valid Linux group name, stored in lower case.
    name TEXT NOT NULL UNIQUE CHECK (name = lower(name) AND length(name) BETWEEN 1 AND 32),
    description TEXT NOT NULL DEFAULT '' CHECK (length(description) <= 200),
    builtin INTEGER NOT NULL DEFAULT 0 CHECK (builtin IN (0, 1)),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    CHECK ((builtin = 1 AND id = 100) OR (builtin = 0 AND id BETWEEN 30000 AND 39999))
) STRICT;

CREATE TABLE group_members (
    group_id INTEGER NOT NULL REFERENCES groups (id) ON DELETE CASCADE,
    user_id INTEGER NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    PRIMARY KEY (group_id, user_id)
) STRICT;

CREATE INDEX group_members_user_id ON group_members (user_id);

INSERT INTO groups (id, name, description, builtin, created_at, updated_at)
VALUES (100, 'users', '', 1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'), strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
