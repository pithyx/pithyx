-- The folders a version holds (#395), as restic stored them (the live paths), so browsing and restoring a version
-- knows its folders without starting restic. JSON array; null for versions read before this column, until the next
-- run or browse of the job reads them again.
ALTER TABLE backup_versions ADD COLUMN paths TEXT CHECK (paths IS NULL OR json_valid(paths));
