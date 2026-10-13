-- Homes (D348): every user's home dataset <pool>/pithyx/homes/<user> is a row in shares of its own, so Files, the
-- recycle bin, the ACLs and every protocol treat it like a shared folder. Its owner reads and writes it, administrators
-- too, nobody else; it is never granted. A home whose user was deleted stays as a row without a user until an
-- administrator deletes it or moves its files into a shared folder. The name of a home row is "home:<folder>" and never
-- shown: users see "home" for their own and the folder name below "homes".
ALTER TABLE shares ADD COLUMN home INTEGER NOT NULL DEFAULT 0 CHECK (home IN (0, 1));
ALTER TABLE shares ADD COLUMN home_user_id INTEGER REFERENCES users (id) ON DELETE SET NULL;

CREATE UNIQUE INDEX shares_home_user_id ON shares (home_user_id) WHERE home_user_id IS NOT NULL;

-- A user's own quota on its home in bytes: NULL takes the default (setting homes.quota), 0 means no quota.
ALTER TABLE users ADD COLUMN home_quota_bytes INTEGER CHECK (home_quota_bytes IS NULL OR home_quota_bytes >= 0);
