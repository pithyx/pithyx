-- File services (step 12, D287): SMB and SFTP for the box, per user and per shared folder. Administrators use every
-- protocol whatever their row says.

-- One row: whether SMB and SFTP run, and the SMB workgroup.
CREATE TABLE file_services (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    smb_enabled INTEGER NOT NULL CHECK (smb_enabled IN (0, 1)),
    smb_workgroup TEXT NOT NULL CHECK (length(smb_workgroup) BETWEEN 1 AND 15),
    sftp_enabled INTEGER NOT NULL CHECK (sftp_enabled IN (0, 1))
) STRICT;

INSERT INTO file_services (id, smb_enabled, smb_workgroup, sftp_enabled) VALUES (1, 1, 'WORKGROUP', 0);

-- Protocol rights of a user: SMB on and SFTP off unless an administrator changes them.
ALTER TABLE users ADD COLUMN smb INTEGER NOT NULL DEFAULT 1 CHECK (smb IN (0, 1));
ALTER TABLE users ADD COLUMN sftp INTEGER NOT NULL DEFAULT 0 CHECK (sftp IN (0, 1));

-- Protocols of a shared folder, and whether SMB clients must encrypt.
ALTER TABLE shares ADD COLUMN smb INTEGER NOT NULL DEFAULT 1 CHECK (smb IN (0, 1));
ALTER TABLE shares ADD COLUMN sftp INTEGER NOT NULL DEFAULT 1 CHECK (sftp IN (0, 1));
ALTER TABLE shares ADD COLUMN smb_encrypt INTEGER NOT NULL DEFAULT 0 CHECK (smb_encrypt IN (0, 1));
