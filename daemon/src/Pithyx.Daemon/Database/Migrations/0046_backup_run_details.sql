-- The activity of the Backup app (#402, D416): the files a backup run could not read, as a JSON array of
-- {"path": "<share>/<folder>/<file>", "reason": "permission" | "missing" | "other"} of at most 100 entries, and an
-- index for the runs of every job by time.
ALTER TABLE backup_runs ADD COLUMN unreadable TEXT CHECK (unreadable IS NULL OR json_valid(unreadable));

CREATE INDEX backup_runs_started ON backup_runs (started_at);
