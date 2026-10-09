-- The Store's catalogs and updates (S3, D328, D329). A catalog is a Git repository whose branch catalog (channel
-- stable) or catalog-next (channel beta) the box fetches into /var/lib/pithyx/catalogs/<id>/ and verifies. The two
-- catalogs of the project are seeded, the community catalog off; administrators add custom ones. Everything here is in
-- the backup part "Apps".
CREATE TABLE catalogs (
    id INTEGER PRIMARY KEY,
    -- The id of the catalog's catalog.json; a custom catalog has none until its first fetch.
    catalog_id TEXT UNIQUE CHECK (catalog_id IS NULL OR (length(catalog_id) BETWEEN 5 AND 100 AND catalog_id NOT GLOB '*[^a-z0-9.-]*')),
    -- official and community are the project's (signed, keys built into the daemon); custom ones are unverified.
    kind TEXT NOT NULL CHECK (kind IN ('official', 'community', 'custom')),
    -- An https:// or SSH (git@host:path) URL of the repository; file:// only in simulation.
    url TEXT NOT NULL UNIQUE CHECK (length(url) BETWEEN 1 AND 500),
    enabled INTEGER NOT NULL DEFAULT 1 CHECK (enabled IN (0, 1)),
    -- The deploy key the box made for an SSH URL: the private key in OpenSSH format and its public line.
    ssh_private_key TEXT,
    ssh_public_key TEXT,
    -- The server's host key as a known_hosts line, pinned at the first connection (GitHub's keys are built in).
    host_key TEXT,
    -- An optional token to pull the catalog's private images, as user:token.
    registry_token TEXT,
    -- The highest sequence verified per channel; a lower one is refused as a replay.
    sequence_stable INTEGER,
    sequence_beta INTEGER,
    -- The last fetch: when it ended, the commit of the verified copy, and the error of a failed one (code and JSON params).
    checked_at TEXT,
    verified_at TEXT,
    verified_commit TEXT,
    error_code TEXT,
    error_params TEXT CHECK (error_params IS NULL OR json_valid(error_params)),
    added_at TEXT NOT NULL
) STRICT;

INSERT INTO catalogs (catalog_id, kind, url, enabled, added_at) VALUES
    ('org.pithyx.official', 'official', 'https://github.com/pithyx/apps-official.git', 1, strftime('%Y-%m-%dT%H:%M:%SZ', 'now')),
    ('org.pithyx.community', 'community', 'https://github.com/pithyx/apps-community.git', 0, strftime('%Y-%m-%dT%H:%M:%SZ', 'now'));

-- The catalog an app came from (its catalog_id; null for a sideload), the update policy that overrides the box's
-- (null follows it), and the version before the last update with its snapshot, for "back to version x" (JSON).
ALTER TABLE apps ADD COLUMN catalog_id TEXT CHECK (catalog_id IS NULL OR length(catalog_id) BETWEEN 5 AND 100);
ALTER TABLE apps ADD COLUMN update_policy TEXT CHECK (update_policy IS NULL OR update_policy IN ('automatic', 'notify', 'none'));
ALTER TABLE apps ADD COLUMN previous TEXT CHECK (previous IS NULL OR json_valid(previous));

INSERT INTO settings (key, value) VALUES
    ('store.updates.policy', 'notify'),
    ('store.updates.time', '03:00'),
    ('store.channel', 'stable')
ON CONFLICT (key) DO NOTHING;

-- Automatic updates run as jobs of the box without a user, which administrators see (D329): user_id may be null now.
-- Jobs are runtime state, so the table is rebuilt with its rows.
CREATE TABLE jobs_new (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    -- Null for a job of the box itself.
    user_id INTEGER REFERENCES users (id) ON DELETE CASCADE,
    kind TEXT NOT NULL,
    state TEXT NOT NULL CHECK (state IN ('queued', 'running', 'succeeded', 'failed', 'cancelled', 'interrupted')),
    params TEXT,
    result TEXT,
    error_code TEXT,
    error_params TEXT,
    items_done INTEGER NOT NULL DEFAULT 0,
    items_total INTEGER NOT NULL DEFAULT 0,
    bytes_done INTEGER NOT NULL DEFAULT 0,
    bytes_total INTEGER NOT NULL DEFAULT 0,
    current_item TEXT,
    request_id TEXT NOT NULL,
    created_at TEXT NOT NULL,
    started_at TEXT,
    finished_at TEXT
) STRICT;

INSERT INTO jobs_new SELECT * FROM jobs;
DROP TABLE jobs;
ALTER TABLE jobs_new RENAME TO jobs;
CREATE INDEX jobs_user_id_created_at ON jobs (user_id, created_at);
