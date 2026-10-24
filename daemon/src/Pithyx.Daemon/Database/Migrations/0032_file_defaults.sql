-- The way a user opens files of an extension, chosen with "Always use this app" (D371): the viewer, a download or an
-- installed app (app:<id>). The same on every device; without a row the app the manifests name as default decides.
CREATE TABLE user_file_defaults (
    user_id INTEGER NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    extension TEXT NOT NULL,
    handler TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    PRIMARY KEY (user_id, extension)
) STRICT;
