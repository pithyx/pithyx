-- A deactivated user cannot sign in, and the Linux account is expired; NULL while the user is active.
ALTER TABLE users ADD COLUMN disabled_at TEXT;

-- The POSIX ACL last set on the share's folder tree (the setfacl entries, D286), so a restart does not walk every share
-- again; NULL until it was set.
ALTER TABLE shares ADD COLUMN acl TEXT;
