-- WebDAV and FTP (step 12, D291): both off for the box until an administrator switches them on, and offered by every
-- shared folder like SMB and SFTP, so the box setting and the users' rights decide.
ALTER TABLE file_services ADD COLUMN webdav_enabled INTEGER NOT NULL DEFAULT 0 CHECK (webdav_enabled IN (0, 1));
ALTER TABLE file_services ADD COLUMN ftp_enabled INTEGER NOT NULL DEFAULT 0 CHECK (ftp_enabled IN (0, 1));
-- Plain FTP without TLS, at the users' own risk: passwords and files cross the network readable.
ALTER TABLE file_services ADD COLUMN ftp_plain INTEGER NOT NULL DEFAULT 0 CHECK (ftp_plain IN (0, 1));

ALTER TABLE shares ADD COLUMN webdav INTEGER NOT NULL DEFAULT 1 CHECK (webdav IN (0, 1));
ALTER TABLE shares ADD COLUMN ftp INTEGER NOT NULL DEFAULT 1 CHECK (ftp IN (0, 1));
