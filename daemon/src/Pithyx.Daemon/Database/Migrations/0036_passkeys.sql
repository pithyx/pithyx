-- Passkeys (D380): WebAuthn credentials a user signs in to the desktop with instead of the password and TOTP. A
-- passkey belongs to the name the box was opened under (rp_id); the same box under another name needs another one.
CREATE TABLE passkeys (
    id INTEGER PRIMARY KEY,
    user_id INTEGER NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    credential_id BLOB NOT NULL UNIQUE,
    public_key BLOB NOT NULL,
    sign_count INTEGER NOT NULL,
    rp_id TEXT NOT NULL,
    name TEXT NOT NULL,
    transports TEXT NOT NULL,
    backed_up INTEGER NOT NULL,
    created_at TEXT NOT NULL,
    last_used_at TEXT
) STRICT;

CREATE INDEX passkeys_user ON passkeys (user_id);
