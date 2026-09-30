-- Every user becomes a Linux account with a uid from the Pithyx range (D103): users 20000 to 29999, groups 30000 to
-- 39999, apps 40000 to 49999. Numbers are allocated once and never reused, not even after the user is deleted, so a new
-- user never inherits old files. Timestamps are ISO 8601 UTC text.

CREATE TABLE id_allocations (
    id INTEGER PRIMARY KEY,
    kind TEXT NOT NULL CHECK (kind IN ('user', 'group', 'app')),
    name TEXT NOT NULL,
    allocated_at TEXT NOT NULL,
    CHECK (
        (kind = 'user' AND id BETWEEN 20000 AND 29999)
        OR (kind = 'group' AND id BETWEEN 30000 AND 39999)
        OR (kind = 'app' AND id BETWEEN 40000 AND 49999)
    )
) STRICT;

ALTER TABLE users ADD COLUMN uid INTEGER REFERENCES id_allocations (id);

CREATE UNIQUE INDEX users_uid ON users (uid);

-- Existing users (the administrator from the setup) get their uids in the order they were created.
INSERT INTO id_allocations (id, kind, name, allocated_at)
SELECT 19999 + row_number() OVER (ORDER BY id), 'user', username, strftime('%Y-%m-%dT%H:%M:%fZ', 'now') FROM users;

UPDATE users SET uid = (SELECT a.id FROM id_allocations a WHERE a.kind = 'user' AND a.name = users.username);
