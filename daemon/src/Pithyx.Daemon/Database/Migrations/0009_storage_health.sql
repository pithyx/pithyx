-- Storage health (step 8.2). Alerts themselves are derived from the live state and never stored; only mutes are.
-- A mute hides one alert for one user at its severity. The daemon deletes it when the alert goes away, so the alert
-- is shown again when its cause comes back.

CREATE TABLE storage_alert_mutes (
    user_id INTEGER NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    -- For example pool.degraded:tank or disk.smart_failed:wwn-0x5000c500a1b2c3d4.
    alert_key TEXT NOT NULL CHECK (length(alert_key) BETWEEN 1 AND 300),
    severity TEXT NOT NULL CHECK (severity IN ('warning', 'critical')),
    muted_at TEXT NOT NULL,
    PRIMARY KEY (user_id, alert_key)
) STRICT;

-- Schedules of scrubs (one per pool) and SMART self-tests (one per kind, for every disk). A missing row means the
-- default: scrub monthly, short self-test weekly, long self-test monthly.
CREATE TABLE storage_schedules (
    kind TEXT NOT NULL CHECK (kind IN ('scrub', 'shortSelfTest', 'longSelfTest')),
    -- The pool name for scrubs; empty for self-tests.
    target TEXT NOT NULL,
    frequency TEXT NOT NULL CHECK (frequency IN ('off', 'weekly', 'monthly')),
    updated_at TEXT NOT NULL,
    PRIMARY KEY (kind, target)
) STRICT;

-- When a scheduled task last ran per pool or disk. A pool or disk seen for the first time gets the time it was seen,
-- so a new install does not start every scrub and self-test at once.
CREATE TABLE storage_schedule_runs (
    kind TEXT NOT NULL CHECK (kind IN ('scrub', 'shortSelfTest', 'longSelfTest')),
    -- The pool name for scrubs, the disk id for self-tests.
    target TEXT NOT NULL,
    last_run_at TEXT NOT NULL,
    PRIMARY KEY (kind, target)
) STRICT;
