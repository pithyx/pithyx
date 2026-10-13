-- D346: certificates the box's own authority issued for other devices. Only the certificate is kept, never its key;
-- the chain holds the intermediate it was issued under, and the root's fingerprint tells whether a newer authority
-- replaced the one that issued it. created_by is the administrator's name, so restoring certificates alone never
-- breaks a key to users.
CREATE TABLE tls_issued (
    id INTEGER PRIMARY KEY,
    certificate TEXT NOT NULL,
    chain TEXT NOT NULL,
    key_type TEXT NOT NULL CHECK (key_type IN ('ecdsa', 'rsa')),
    root_fingerprint TEXT NOT NULL,
    created_at TEXT NOT NULL,
    created_by TEXT
) STRICT;
