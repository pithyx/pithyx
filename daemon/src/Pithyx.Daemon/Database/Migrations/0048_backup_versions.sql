-- The versions of a backup job (#404): the restic snapshots in its repository, kept here after every run and every
-- change of a version, because no read request may start restic. In the backup part "Backups" with the jobs.
CREATE TABLE backup_versions (
    id INTEGER PRIMARY KEY,
    job_id INTEGER NOT NULL REFERENCES backup_jobs (id) ON DELETE CASCADE,
    -- The snapshot's id now; restic gives a snapshot a new id when its tags change.
    snapshot TEXT NOT NULL,
    -- The id the snapshot was made with (restic's "original"), which stays.
    origin TEXT NOT NULL,
    time TEXT NOT NULL,
    files INTEGER,
    bytes INTEGER,
    added_bytes INTEGER,
    files_added INTEGER,
    files_changed INTEGER,
    -- Files gone since the previous version; null when it was not compared.
    files_removed INTEGER,
    -- Kept by the tag pithyx-protected, whatever the retention says.
    protected INTEGER NOT NULL DEFAULT 0 CHECK (protected IN (0, 1)),
    -- JSON array of why the last retention kept it: last, hourly, daily, weekly, monthly, yearly, protected.
    keep TEXT NOT NULL DEFAULT '[]' CHECK (json_valid(keep)),
    -- JSON array of at most 100 {"path": "<share>/<folder>/<file>", "change": "added" | "modified" | "removed"}
    -- against the previous version; null when it was not compared.
    changes TEXT CHECK (changes IS NULL OR json_valid(changes)),
    UNIQUE (job_id, origin)
) STRICT;

CREATE INDEX backup_versions_job ON backup_versions (job_id, time);
