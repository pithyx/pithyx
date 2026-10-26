-- Elevated rights of an app's backend (D311): each right of the manifest's backend.elevated that an administrator
-- granted after a step-up with password and TOTP, or revoked. An update grants new ones only after a new step-up; a
-- revoked one stays revoked until an administrator grants it again, and the backend runs without it.
CREATE TABLE app_elevated (
    app_id TEXT NOT NULL REFERENCES apps (id) ON DELETE CASCADE,
    "right" TEXT NOT NULL CHECK (length("right") BETWEEN 1 AND 200),
    granted INTEGER NOT NULL CHECK (granted IN (0, 1)),
    changed_at TEXT NOT NULL,
    -- The administrator who granted or revoked it last; the name stays when the account goes.
    changed_by TEXT,
    PRIMARY KEY (app_id, "right")
) STRICT;

-- What administrators did with elevated rights and every step-up, kept apart from the Log Center's retention and never
-- replaced by a restore (D311). Each row also goes to the Log Center under the source `audit`.
CREATE TABLE audit_log (
    id INTEGER PRIMARY KEY,
    at TEXT NOT NULL,
    user_id INTEGER,
    username TEXT,
    action TEXT NOT NULL,
    target TEXT,
    params TEXT,
    request_id TEXT
) STRICT;

CREATE INDEX audit_log_at ON audit_log (at);
