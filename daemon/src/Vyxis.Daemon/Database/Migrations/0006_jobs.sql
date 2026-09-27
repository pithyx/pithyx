-- Background jobs for long operations such as file copies. Jobs are never resumed after a restart: the daemon marks
-- queued and running ones as interrupted on start. Finished jobs are deleted 7 days after they finished.

CREATE TABLE jobs (
    -- AUTOINCREMENT: ids name realtime topics (jobs/<id>) and must never be reused after a cleanup.
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id INTEGER NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    kind TEXT NOT NULL,
    state TEXT NOT NULL CHECK (state IN ('queued', 'running', 'succeeded', 'failed', 'cancelled', 'interrupted')),
    -- JSON texts.
    params TEXT,
    result TEXT,
    error_code TEXT,
    error_params TEXT,
    items_done INTEGER NOT NULL DEFAULT 0,
    items_total INTEGER NOT NULL DEFAULT 0,
    bytes_done INTEGER NOT NULL DEFAULT 0,
    bytes_total INTEGER NOT NULL DEFAULT 0,
    current_item TEXT,
    -- W3C trace id of the request that started the job; its logs and commands carry it.
    request_id TEXT NOT NULL,
    created_at TEXT NOT NULL,
    started_at TEXT,
    finished_at TEXT
) STRICT;

CREATE INDEX jobs_user_id_created_at ON jobs (user_id, created_at);
