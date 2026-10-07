-- The log level of each third-party app (A12, D305): with info the app sends info, warn and error to the daemon;
-- debug, which an administrator switches on in the Store, adds its debug entries. The app learns the level from its
-- context and from logLevelChange.

ALTER TABLE apps ADD COLUMN log_level TEXT NOT NULL DEFAULT 'info' CHECK (log_level IN ('info', 'debug'));
