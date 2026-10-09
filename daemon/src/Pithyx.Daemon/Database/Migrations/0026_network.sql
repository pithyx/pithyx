-- Network settings (step 14.2, D110, D292, D337): what Pithyx configured (the JSON of NetworkConfig), and the one
-- change that waits for its confirmation, with the settings, files and host name it replaced, so a daemon that restarts
-- in between rolls it back like the timer would. Neither is restored from a configuration backup (D337).

CREATE TABLE network_config (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    config TEXT NOT NULL,
    updated_at TEXT NOT NULL
) STRICT;

CREATE TABLE network_pending (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    change_id TEXT NOT NULL,
    previous_config TEXT,
    previous_files TEXT NOT NULL,
    previous_host_name TEXT NOT NULL,
    change TEXT NOT NULL,
    deadline TEXT NOT NULL
) STRICT;
