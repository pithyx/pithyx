-- The permission model (step 11, D290): rights are grants to a subject (a user, a group, later an app) on a resource,
-- and a user's effective rights are the union of its own grants and those of its groups; there are no deny rules,
-- and administrators have everything. Resources:
--   share:<id>                        read or readWrite
--   protocol:smb|sftp|webdav|ftp      allow
--   app:<id>                          allow
--   right:admin|desktopLogin|sshLogin|power   allow
-- A subject has at most one grant per resource. Grants of a deleted user, group or share go with it.

CREATE TABLE grants (
    subject_kind TEXT NOT NULL CHECK (subject_kind IN ('user', 'group', 'app')),
    subject_id INTEGER NOT NULL,
    resource TEXT NOT NULL CHECK (
        resource IN ('right:admin', 'right:desktopLogin', 'right:sshLogin', 'right:power',
            'protocol:smb', 'protocol:sftp', 'protocol:webdav', 'protocol:ftp')
        OR resource GLOB 'app:[a-z]*'
        OR resource GLOB 'share:[1-9]*'
    ),
    level TEXT NOT NULL CHECK (
        (resource GLOB 'share:*' AND level IN ('read', 'readWrite')) OR (resource NOT GLOB 'share:*' AND level = 'allow')
    ),
    PRIMARY KEY (subject_kind, subject_id, resource)
) STRICT;

CREATE INDEX grants_resource ON grants (resource);

CREATE TRIGGER users_grants_delete AFTER DELETE ON users
BEGIN
    DELETE FROM grants WHERE subject_kind = 'user' AND subject_id = old.id;
END;

CREATE TRIGGER groups_grants_delete AFTER DELETE ON groups
BEGIN
    DELETE FROM grants WHERE subject_kind = 'group' AND subject_id = old.id;
END;

CREATE TRIGGER shares_grants_delete AFTER DELETE ON shares
BEGIN
    DELETE FROM grants WHERE resource = 'share:' || old.id;
END;

-- Everything users could do before becomes their own grant, so nobody gains or loses a right: share access, the
-- administrator right, signing in to the desktop (everybody could), SMB and SFTP, and the apps everybody saw.
INSERT INTO grants (subject_kind, subject_id, resource, level)
SELECT 'user', user_id, 'share:' || share_id, level FROM share_access;

INSERT INTO grants (subject_kind, subject_id, resource, level)
SELECT 'user', id, 'right:admin', 'allow' FROM users WHERE is_admin = 1;

INSERT INTO grants (subject_kind, subject_id, resource, level)
SELECT 'user', id, 'right:desktopLogin', 'allow' FROM users;

INSERT INTO grants (subject_kind, subject_id, resource, level)
SELECT 'user', id, 'protocol:smb', 'allow' FROM users WHERE smb = 1 AND is_admin = 0;

INSERT INTO grants (subject_kind, subject_id, resource, level)
SELECT 'user', id, 'protocol:sftp', 'allow' FROM users WHERE sftp = 1 AND is_admin = 0;

INSERT INTO grants (subject_kind, subject_id, resource, level)
SELECT 'user', users.id, 'app:' || apps.id, 'allow'
FROM users, (SELECT 'files' AS id UNION ALL SELECT 'viewer' UNION ALL SELECT 'demo') AS apps
WHERE users.is_admin = 0;

DROP TABLE share_access;

ALTER TABLE users DROP COLUMN is_admin;
ALTER TABLE users DROP COLUMN smb;
ALTER TABLE users DROP COLUMN sftp;

-- The two Linux groups of a shared folder on disk (D102): members with read and members with read and write. Their
-- gids come from the group range and are never reused; NULL until the daemon allocated them.
ALTER TABLE shares ADD COLUMN read_gid INTEGER REFERENCES id_allocations (id);
ALTER TABLE shares ADD COLUMN write_gid INTEGER REFERENCES id_allocations (id);

-- SSH logins with a shell for users with the SSH login right; off until an administrator switches it on.
ALTER TABLE file_services ADD COLUMN ssh_enabled INTEGER NOT NULL DEFAULT 0 CHECK (ssh_enabled IN (0, 1));
