-- Desktop items (step 5): app shortcuts and sticky notes on a grid of cells, shared across the user's devices. Every
-- change increments the item's version, and a change that names an older version is refused, so two devices cannot
-- overwrite each other unnoticed. Files and folders join with the file manager (step 6); the kind 'file' stays.

ALTER TABLE desktop_items DROP COLUMN position;
ALTER TABLE desktop_items ADD COLUMN grid_column INTEGER NOT NULL DEFAULT 0 CHECK (grid_column >= 0);
ALTER TABLE desktop_items ADD COLUMN grid_row INTEGER NOT NULL DEFAULT 0 CHECK (grid_row >= 0);
ALTER TABLE desktop_items ADD COLUMN version INTEGER NOT NULL DEFAULT 1;

-- Existing users get the default shortcuts that new users get at creation.
INSERT INTO desktop_items (user_id, kind, data, grid_column, grid_row, created_at, updated_at)
SELECT users.id, 'shortcut', json_object('app', defaults.app), 0, defaults.grid_row, users.created_at, users.created_at
FROM users, (SELECT 'disks' AS app, 0 AS grid_row UNION ALL SELECT 'about', 1) AS defaults
ORDER BY users.id, defaults.grid_row;
