-- Layouts are saved over REST with optimistic concurrency: every save increments the version, and a save that names
-- an older version (If-Match) is refused, so two tabs of one browser cannot overwrite each other unnoticed.

ALTER TABLE desktop_layouts ADD COLUMN version INTEGER NOT NULL DEFAULT 1;
