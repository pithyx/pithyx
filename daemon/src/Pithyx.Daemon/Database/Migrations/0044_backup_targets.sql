-- Where the backup app sends backups (#393, D414): servers reached over SFTP with an SSH key the box made for the
-- target and the host key the administrator confirmed. Everything here is in the backup part "Backups".
CREATE TABLE backup_targets (
    id INTEGER PRIMARY KEY,
    name TEXT NOT NULL UNIQUE COLLATE NOCASE CHECK (length(name) BETWEEN 1 AND 64),
    kind TEXT NOT NULL CHECK (kind IN ('sftp')),
    host TEXT NOT NULL CHECK (length(host) BETWEEN 1 AND 253),
    port INTEGER NOT NULL CHECK (port BETWEEN 1 AND 65535),
    user_name TEXT NOT NULL CHECK (length(user_name) BETWEEN 1 AND 64),
    -- The folder on the server, relative to the login folder or absolute; empty is the login folder.
    path TEXT NOT NULL CHECK (length(path) <= 500),
    -- The confirmed host key as "<type> <base64>"; every connection accepts only this key.
    host_key TEXT NOT NULL,
    -- The target's own key: the private key in OpenSSH format and its public line.
    ssh_private_key TEXT NOT NULL,
    ssh_public_key TEXT NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    -- The last connection test: when, and the error of a failed one (code and JSON params).
    tested_at TEXT,
    test_error_code TEXT,
    test_error_params TEXT CHECK (test_error_params IS NULL OR json_valid(test_error_params))
) STRICT;
