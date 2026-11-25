-- Mirrors (#397, D421): one current copy of folders of this box on a target as plain files, written by rsync over
-- SSH from the run's ZFS snapshots, without versions. A mirror is a backup job of the kind 'mirror' whose repository
-- is its folder on the target. pull_delete becomes delete_removed, which a mirror uses too. SQLite cannot change a
-- CHECK, so backup_jobs is rebuilt as in 0050, with its runs and versions kept aside meanwhile.
CREATE TEMP TABLE backup_runs_kept AS SELECT * FROM backup_runs;
CREATE TEMP TABLE backup_versions_kept AS SELECT * FROM backup_versions;
DELETE FROM backup_versions;
DELETE FROM backup_runs;

CREATE TABLE backup_jobs_new (
    id INTEGER PRIMARY KEY,
    name TEXT NOT NULL UNIQUE COLLATE NOCASE CHECK (length(name) BETWEEN 1 AND 64),
    kind TEXT NOT NULL CHECK (kind IN ('versioned', 'pull', 'mirror')),
    -- The target of a versioned backup or a mirror, the server of a pull.
    target_id INTEGER NOT NULL REFERENCES backup_targets (id) ON DELETE RESTRICT,
    -- The repository's folder below the target's path, and its restic password; a mirror's folder there and no
    -- password; empty for a pull.
    repository TEXT NOT NULL CHECK (kind = 'pull' OR length(repository) BETWEEN 1 AND 200),
    repository_key TEXT NOT NULL,
    -- JSON array of {"shareId": n, "path": "folder/below"}; empty for a pull.
    sources TEXT NOT NULL CHECK (json_valid(sources)),
    -- JSON array of restic exclude patterns, or of rsync exclude patterns for a pull.
    excludes TEXT NOT NULL DEFAULT '[]' CHECK (json_valid(excludes)),
    schedule TEXT NOT NULL,
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    keep_last INTEGER NOT NULL DEFAULT 0 CHECK (keep_last >= 0),
    keep_hourly INTEGER NOT NULL DEFAULT 0 CHECK (keep_hourly >= 0),
    keep_daily INTEGER NOT NULL DEFAULT 0 CHECK (keep_daily >= 0),
    keep_weekly INTEGER NOT NULL DEFAULT 0 CHECK (keep_weekly >= 0),
    keep_monthly INTEGER NOT NULL DEFAULT 0 CHECK (keep_monthly >= 0),
    keep_yearly INTEGER NOT NULL DEFAULT 0 CHECK (keep_yearly >= 0),
    check_schedule TEXT,
    check_read_percent INTEGER NOT NULL DEFAULT 0 CHECK (check_read_percent BETWEEN 0 AND 100),
    upload_limit_kib INTEGER NOT NULL DEFAULT 0 CHECK (upload_limit_kib >= 0),
    download_limit_kib INTEGER NOT NULL DEFAULT 0 CHECK (download_limit_kib >= 0),
    overdue_hours INTEGER CHECK (overdue_hours IS NULL OR overdue_hours >= 1),
    overdue_notified_at TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    -- A pull's destination: the shared folder, the folder below it the box made as a dataset of its own, and that
    -- dataset's name. Not a foreign key: a shared folder may go, and the next run says so.
    destination_share_id INTEGER,
    destination_folder TEXT,
    destination_dataset TEXT,
    -- JSON array of the server's folders, each starting with "/" below the folder the server's key allows.
    remote_paths TEXT CHECK (remote_paths IS NULL OR json_valid(remote_paths)),
    -- Whether files gone on the server (pull) or on the box (mirror) are removed from the copy.
    delete_removed INTEGER CHECK (delete_removed IS NULL OR delete_removed IN (0, 1)),
    -- 'server' keeps the server's owners, modes and ACLs as numbers; 'box' gives files the shared folder's rights.
    pull_owners TEXT CHECK (pull_owners IS NULL OR pull_owners IN ('server', 'box')),
    CHECK (kind <> 'pull' OR (destination_share_id IS NOT NULL AND destination_folder IS NOT NULL AND destination_dataset IS NOT NULL
        AND remote_paths IS NOT NULL AND delete_removed IS NOT NULL AND pull_owners IS NOT NULL)),
    CHECK (kind <> 'mirror' OR (delete_removed IS NOT NULL AND repository_key = ''))
) STRICT;

INSERT INTO backup_jobs_new (id, name, kind, target_id, repository, repository_key, sources, excludes, schedule, enabled,
    keep_last, keep_hourly, keep_daily, keep_weekly, keep_monthly, keep_yearly, check_schedule, check_read_percent,
    upload_limit_kib, download_limit_kib, overdue_hours, overdue_notified_at, created_at, updated_at,
    destination_share_id, destination_folder, destination_dataset, remote_paths, delete_removed, pull_owners)
SELECT id, name, kind, target_id, repository, repository_key, sources, excludes, schedule, enabled,
    keep_last, keep_hourly, keep_daily, keep_weekly, keep_monthly, keep_yearly, check_schedule, check_read_percent,
    upload_limit_kib, download_limit_kib, overdue_hours, overdue_notified_at, created_at, updated_at,
    destination_share_id, destination_folder, destination_dataset, remote_paths, pull_delete, pull_owners
FROM backup_jobs;
DROP TABLE backup_jobs;
ALTER TABLE backup_jobs_new RENAME TO backup_jobs;
CREATE INDEX backup_jobs_target ON backup_jobs (target_id);

INSERT INTO backup_runs SELECT * FROM temp.backup_runs_kept;
INSERT INTO backup_versions SELECT * FROM temp.backup_versions_kept;
DROP TABLE temp.backup_runs_kept;
DROP TABLE temp.backup_versions_kept;
