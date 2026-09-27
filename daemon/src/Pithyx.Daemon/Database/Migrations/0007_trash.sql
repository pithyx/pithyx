-- Recycle bin per share (step 6). Deleted items are moved to the hidden folder .pithyx-trash at the share root, each
-- under its row id; the row keeps where it came from. Items older than the share's retention are deleted for good.

ALTER TABLE shares ADD COLUMN trash_retention_days INTEGER NOT NULL DEFAULT 30 CHECK (trash_retention_days BETWEEN 0 AND 3650);

CREATE TABLE trash_items (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    share_id INTEGER NOT NULL REFERENCES shares (id) ON DELETE CASCADE,
    -- Path relative to the share root the item had when it was deleted.
    original_path TEXT NOT NULL,
    kind TEXT NOT NULL CHECK (kind IN ('file', 'folder', 'symlink', 'other')),
    size INTEGER NOT NULL,
    deleted_by INTEGER REFERENCES users (id) ON DELETE SET NULL,
    deleted_at TEXT NOT NULL
) STRICT;

CREATE INDEX trash_items_share_deleted ON trash_items (share_id, deleted_at);
