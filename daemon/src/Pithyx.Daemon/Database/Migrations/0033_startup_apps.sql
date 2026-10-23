-- The apps a user opens after signing in, in the order they open (D373). The same on every device; the desktop skips
-- an app that is gone or that the user may no longer use.
CREATE TABLE user_startup_apps (
    user_id INTEGER NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    app_id TEXT NOT NULL,
    position INTEGER NOT NULL,
    PRIMARY KEY (user_id, app_id)
) STRICT;
