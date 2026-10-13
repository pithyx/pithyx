-- D347: the reverse proxy in Settings. Proxy hosts and redirect hosts answer on 443 and 80 by host name, streams
-- forward a TCP or UDP port. Lists are JSON arrays: domains in lower case (a leading *. for a wildcard of one label),
-- headers as {"name","value"} objects, addresses as IPs or CIDR ranges, Basic Auth users as {"username","hash"} with an
-- Argon2id hash, and the users and groups of the Pithyx sign-in by name, so restoring the proxy alone never points
-- at another account. An uploaded certificate lives next to the box's own in the TLS directory, never here.
CREATE TABLE proxy_hosts (
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
    certificate TEXT NOT NULL CHECK (certificate IN ('box', 'custom')),
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

CREATE TABLE proxy_redirects (
    id INTEGER PRIMARY KEY,
    domains TEXT NOT NULL,
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    target TEXT NOT NULL,
    status INTEGER NOT NULL CHECK (status IN (301, 302, 307, 308)),
    preserve_path INTEGER NOT NULL CHECK (preserve_path IN (0, 1)),
    certificate TEXT NOT NULL CHECK (certificate IN ('box', 'custom')),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
) STRICT;

CREATE TABLE proxy_streams (
    id INTEGER PRIMARY KEY,
    name TEXT NOT NULL,
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1)),
    port INTEGER NOT NULL CHECK (port BETWEEN 1 AND 65535),
    tcp INTEGER NOT NULL CHECK (tcp IN (0, 1)),
    udp INTEGER NOT NULL CHECK (udp IN (0, 1)),
    target_host TEXT NOT NULL,
    target_port INTEGER NOT NULL CHECK (target_port BETWEEN 1 AND 65535),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    CHECK (tcp = 1 OR udp = 1)
) STRICT;

-- Runtime state: a browser signed in to a proxy host through the box. The cookie holds a random token; only its
-- SHA-256 is stored. It ends with the box session it came from and with its proxy host.
CREATE TABLE proxy_sessions (
    id BLOB PRIMARY KEY,
    session_id BLOB NOT NULL REFERENCES sessions (id) ON DELETE CASCADE,
    host_id INTEGER NOT NULL REFERENCES proxy_hosts (id) ON DELETE CASCADE,
    created_at TEXT NOT NULL
) STRICT;

CREATE INDEX proxy_sessions_session_id ON proxy_sessions (session_id);
CREATE INDEX proxy_sessions_host_id ON proxy_sessions (host_id);
