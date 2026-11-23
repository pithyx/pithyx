-- The overview of the Backup app (#403): the size of a job's repository after each backup run (restic stats in
-- raw-data mode, bytes stored on the target), and a target's space as its server reported it over SFTP (statvfs) in
-- the last connection test or after a run, in bytes.
ALTER TABLE backup_runs ADD COLUMN repository_bytes INTEGER CHECK (repository_bytes IS NULL OR repository_bytes >= 0);

ALTER TABLE backup_targets ADD COLUMN space_total_bytes INTEGER CHECK (space_total_bytes IS NULL OR space_total_bytes >= 0);
ALTER TABLE backup_targets ADD COLUMN space_free_bytes INTEGER CHECK (space_free_bytes IS NULL OR space_free_bytes >= 0);
ALTER TABLE backup_targets ADD COLUMN space_at TEXT;
