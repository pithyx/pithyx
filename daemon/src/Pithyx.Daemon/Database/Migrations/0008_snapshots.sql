-- Snapshot schedules per dataset (step 8). A row exists once an administrator saved the schedule; a missing row is
-- an off schedule with the default count. Pithyx prunes only the snapshots a schedule took (pithyx-<kind>-<utc>).

CREATE TABLE snapshot_schedules (
    dataset TEXT NOT NULL,
    kind TEXT NOT NULL CHECK (kind IN ('hourly', 'daily', 'weekly', 'monthly')),
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    keep INTEGER NOT NULL CHECK (keep BETWEEN 1 AND 1000),
    updated_at TEXT NOT NULL,
    PRIMARY KEY (dataset, kind)
) STRICT;
