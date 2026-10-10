-- Updates of the system (D338): every update and rollback with its outcome. The settings of the updater (the daily
-- check, the channel, its time and the last verified channel sequence) live in settings under updates.*. Both are
-- runtime state of this system disk and never part of a configuration backup: another box has its own history.
CREATE TABLE system_updates (
    id INTEGER PRIMARY KEY,
    kind TEXT NOT NULL CHECK (kind IN ('update', 'rollback')),
    -- online (the channel), file (an uploaded file), recovery (the console menu), manual (a rollback from Settings).
    source TEXT NOT NULL CHECK (source IN ('online', 'file', 'recovery', 'manual')),
    from_version TEXT NOT NULL,
    to_version TEXT NOT NULL,
    -- The snapshot taken before an update, or the snapshot a rollback restored.
    snapshot TEXT,
    state TEXT NOT NULL CHECK (state IN ('running', 'restarting', 'succeeded', 'failed', 'rolled_back')),
    error_code TEXT,
    error_params TEXT,
    started_at TEXT NOT NULL,
    finished_at TEXT
) STRICT;
