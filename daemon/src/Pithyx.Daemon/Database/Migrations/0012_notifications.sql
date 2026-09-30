-- The notification history of every user: what jobs, storage alerts and apps told them. Texts are never stored, only
-- a message code and its parameters; the desktop translates them. The daemon keeps at most 200 per user and deletes
-- those older than 30 days.

CREATE TABLE notifications (
    -- AUTOINCREMENT: the desktop keys toasts and dismissals by id, so an id is never reused after a cleanup.
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    user_id INTEGER NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    -- Message code in the notifications namespace, for example jobs.job.failed or app.message.
    code TEXT NOT NULL CHECK (length(code) BETWEEN 1 AND 100),
    -- JSON object of the message parameters.
    params TEXT NOT NULL,
    tone TEXT NOT NULL CHECK (tone IN ('info', 'success', 'warning', 'critical')),
    -- Id of the app that sent it; NULL for the system (jobs, storage).
    app TEXT,
    created_at TEXT NOT NULL,
    read_at TEXT
) STRICT;

CREATE INDEX notifications_user_id ON notifications (user_id, id);
CREATE INDEX notifications_created_at ON notifications (created_at);

-- Storage alerts the administrators were notified about, so a restart does not notify them again for an alert that is
-- still there, and its clearing can say what was resolved. A row is deleted when its alert goes away.
CREATE TABLE storage_alert_notices (
    alert_key TEXT PRIMARY KEY CHECK (length(alert_key) BETWEEN 1 AND 300),
    severity TEXT NOT NULL CHECK (severity IN ('warning', 'critical')),
    -- Message code and JSON parameters of the alert when it was last notified.
    code TEXT NOT NULL,
    params TEXT NOT NULL,
    noticed_at TEXT NOT NULL
) STRICT;
