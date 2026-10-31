-- D378: proxy hosts and redirect hosts may present a certificate from an ACME authority (Let's Encrypt), so the
-- certificate column also takes 'acme'. SQLite cannot change a CHECK, so both tables are rebuilt; the proxy sign-ins
-- (proxy_sessions) would go with the old proxy_hosts and are kept aside meanwhile.
CREATE TEMP TABLE proxy_sessions_kept AS SELECT * FROM proxy_sessions;
DELETE FROM proxy_sessions;

CREATE TABLE proxy_hosts_new (
    id INTEGER PRIMARY KEY,
    domains TEXT NOT NULL,
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    target_scheme TEXT NOT NULL CHECK (target_scheme IN ('http', 'https')),
    target_host TEXT NOT NULL,
    target_port INTEGER NOT NULL CHECK (target_port BETWEEN 1 AND 65535),
    target_tls TEXT NOT NULL CHECK (target_tls IN ('system', 'box', 'none')),
    target_on_box INTEGER NOT NULL CHECK (target_on_box IN (0, 1)),
    websockets INTEGER NOT NULL CHECK (websockets IN (0, 1)),
    http2 INTEGER NOT NULL CHECK (http2 IN (0, 1)),
    certificate TEXT NOT NULL CHECK (certificate IN ('box', 'custom', 'acme')),
    force_https INTEGER NOT NULL CHECK (force_https IN (0, 1)),
    hsts INTEGER NOT NULL CHECK (hsts IN (0, 1)),
    forwarded_headers INTEGER NOT NULL CHECK (forwarded_headers IN (0, 1)),
    preserve_host INTEGER NOT NULL CHECK (preserve_host IN (0, 1)),
    host_header TEXT,
    request_headers TEXT NOT NULL,
    response_headers TEXT NOT NULL,
    max_body_megabytes INTEGER CHECK (max_body_megabytes IS NULL OR max_body_megabytes > 0),
    connect_timeout_seconds INTEGER NOT NULL CHECK (connect_timeout_seconds > 0),
    response_timeout_seconds INTEGER NOT NULL CHECK (response_timeout_seconds > 0),
    allow_addresses TEXT NOT NULL,
    deny_addresses TEXT NOT NULL,
    basic_auth TEXT NOT NULL,
    sign_in INTEGER NOT NULL CHECK (sign_in IN (0, 1)),
    sign_in_users TEXT NOT NULL,
    sign_in_groups TEXT NOT NULL,
    access_log INTEGER NOT NULL CHECK (access_log IN (0, 1)),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
) STRICT;

INSERT INTO proxy_hosts_new SELECT * FROM proxy_hosts;
DROP TABLE proxy_hosts;
ALTER TABLE proxy_hosts_new RENAME TO proxy_hosts;

CREATE TABLE proxy_redirects_new (
    id INTEGER PRIMARY KEY,
    domains TEXT NOT NULL,
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    target TEXT NOT NULL,
    status INTEGER NOT NULL CHECK (status IN (301, 302, 307, 308)),
    preserve_path INTEGER NOT NULL CHECK (preserve_path IN (0, 1)),
    certificate TEXT NOT NULL CHECK (certificate IN ('box', 'custom', 'acme')),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
) STRICT;

INSERT INTO proxy_redirects_new SELECT * FROM proxy_redirects;
DROP TABLE proxy_redirects;
ALTER TABLE proxy_redirects_new RENAME TO proxy_redirects;

INSERT INTO proxy_sessions SELECT * FROM temp.proxy_sessions_kept;
DROP TABLE temp.proxy_sessions_kept;

-- Runtime state of the ACME certificates, one row per route (host:<id> or redirect:<id>): the domains of the last
-- order, how it ended and when the next try is due. The certificates themselves are files in the TLS directory
-- (proxy-host-<id>-acme.pem and .key), which the backup part Certificates takes along; after a restore the files count.
CREATE TABLE proxy_acme (
    route TEXT PRIMARY KEY,
    domains TEXT NOT NULL,
    status TEXT NOT NULL CHECK (status IN ('valid', 'failed')),
    not_after TEXT,
    attempted_at TEXT NOT NULL,
    failures INTEGER NOT NULL CHECK (failures >= 0),
    error_code TEXT,
    error_params TEXT
) STRICT;
