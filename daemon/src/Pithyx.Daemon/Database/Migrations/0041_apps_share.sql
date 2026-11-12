-- The special folder "Apps" (D409, after D314): a system share with one folder per app on the app pool that shows the
-- app's volumes/. It is a row in shares, so its grants, Files and SMB treat it like a shared folder; it has no dataset
-- of its own, is never renamed or deleted, and only administrators have it until others are granted it. The daemon
-- adds the row at start (its folder differs between a box and simulation), named "system:apps" and shown as "Apps".
ALTER TABLE shares ADD COLUMN system TEXT CHECK (system IS NULL OR system = 'apps');

CREATE UNIQUE INDEX shares_system ON shares (system) WHERE system IS NOT NULL;
