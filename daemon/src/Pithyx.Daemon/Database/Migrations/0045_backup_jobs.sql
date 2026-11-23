-- Versioned backups (#394, D415): jobs that back up folders of shared folders and homes with restic over SFTP to a
-- target, and their runs. Both are in the backup part "Backups"; the repository key is kept here, so a restored
-- configuration can read its backups again.
CREATE TABLE backup_jobs (
    id INTEGER PRIMARY KEY,
    name TEXT NOT NULL UNIQUE COLLATE NOCASE CHECK (length(name) BETWEEN 1 AND 64),
    kind TEXT NOT NULL CHECK (kind IN ('versioned')),
    target_id INTEGER NOT NULL REFERENCES backup_targets (id) ON DELETE RESTRICT,
    -- The repository's folder below the target's path.
    repository TEXT NOT NULL CHECK (length(repository) BETWEEN 1 AND 200),
    -- The restic repository's password.
    repository_key TEXT NOT NULL,
    -- JSON array of {"shareId": n, "path": "folder/below"}.
    sources TEXT NOT NULL CHECK (json_valid(sources)),
    -- JSON array of restic exclude patterns.
    excludes TEXT NOT NULL DEFAULT '[]' CHECK (json_valid(excludes)),
    schedule TEXT NOT NULL,
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    keep_last INTEGER NOT NULL DEFAULT 0 CHECK (keep_last >= 0),
    keep_hourly INTEGER NOT NULL DEFAULT 0 CHECK (keep_hourly >= 0),
    keep_daily INTEGER NOT NULL DEFAULT 0 CHECK (keep_daily >= 0),
    keep_weekly INTEGER NOT NULL DEFAULT 0 CHECK (keep_weekly >= 0),
    keep_monthly INTEGER NOT NULL DEFAULT 0 CHECK (keep_monthly >= 0),
    keep_yearly INTEGER NOT NULL DEFAULT 0 CHECK (keep_yearly >= 0),
    -- The integrity check's schedule (null: none) and the share of the data it reads, in percent.
    check_schedule TEXT,
    check_read_percent INTEGER NOT NULL DEFAULT 0 CHECK (check_read_percent BETWEEN 0 AND 100),
    -- Bandwidth limits in KiB/s; 0 is none.
    upload_limit_kib INTEGER NOT NULL DEFAULT 0 CHECK (upload_limit_kib >= 0),
    download_limit_kib INTEGER NOT NULL DEFAULT 0 CHECK (download_limit_kib >= 0),
    -- Hours without a successful run before the job is overdue; null: three times its interval, at least two days.
    overdue_hours INTEGER CHECK (overdue_hours IS NULL OR overdue_hours >= 1),
    -- When administrators were last told the job is overdue, so they are told once per episode.
    overdue_notified_at TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
) STRICT;

CREATE INDEX backup_jobs_target ON backup_jobs (target_id);

CREATE TABLE backup_runs (
    id INTEGER PRIMARY KEY,
    job_id INTEGER NOT NULL REFERENCES backup_jobs (id) ON DELETE CASCADE,
    kind TEXT NOT NULL CHECK (kind IN ('backup', 'check')),
    trigger TEXT NOT NULL CHECK (trigger IN ('schedule', 'manual')),
    -- The run's job on the job board; not a foreign key, the board forgets old jobs.
    board_job_id INTEGER,
    state TEXT NOT NULL CHECK (state IN ('running', 'succeeded', 'warning', 'failed', 'cancelled', 'interrupted')),
    started_at TEXT NOT NULL,
    finished_at TEXT,
    files INTEGER,
    bytes INTEGER,
    added_bytes INTEGER,
    snapshot TEXT,
    warnings INTEGER NOT NULL DEFAULT 0,
    error_code TEXT,
    error_params TEXT CHECK (error_params IS NULL OR json_valid(error_params))
) STRICT;

CREATE INDEX backup_runs_job ON backup_runs (job_id, id);
