-- Share settings (D411, #202): whether a shared folder shows its daily restore points as previous versions (SMB and
-- Files), and each user's quota on a shared folder. ZFS enforces a quota by file owner (userquota@<uid> on the share's
-- dataset); this table is what the box wants, and the daemon sets every dataset to it.
ALTER TABLE shares ADD COLUMN previous_versions INTEGER NOT NULL DEFAULT 1 CHECK (previous_versions IN (0, 1));

CREATE TABLE share_user_quotas (
    share_id INTEGER NOT NULL REFERENCES shares (id) ON DELETE CASCADE,
    user_id INTEGER NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    quota_bytes INTEGER NOT NULL CHECK (quota_bytes >= 1048576),
    PRIMARY KEY (share_id, user_id)
) STRICT;
