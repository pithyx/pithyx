-- The files a user opened recently (#333): one row per user and file, the newest 50 per user. Opening a file again
-- moves its row to the top. A deleted user or shared folder takes its rows along.
CREATE TABLE recent_files (
    user_id INTEGER NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    share_id INTEGER NOT NULL REFERENCES shares (id) ON DELETE CASCADE,
    path TEXT NOT NULL CHECK (length(path) > 0),
    opened_at TEXT NOT NULL,
    PRIMARY KEY (user_id, share_id, path)
) STRICT;

CREATE INDEX recent_files_by_user ON recent_files (user_id, opened_at);
