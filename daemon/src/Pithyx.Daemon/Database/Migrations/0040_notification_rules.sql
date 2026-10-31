-- The cells of the notification matrix an administrator changed (D407): per event the box reports, whether the
-- administrators' desktop (channel_id NULL) or one of the box's channels gets it. A cell without a row keeps its
-- default: the desktop gets everything, a channel what its categories and least level take.
CREATE TABLE notification_rules (
    event TEXT NOT NULL CHECK (length(event) BETWEEN 1 AND 100),
    channel_id INTEGER REFERENCES notification_channels (id) ON DELETE CASCADE,
    enabled INTEGER NOT NULL CHECK (enabled IN (0, 1))
) STRICT;

CREATE UNIQUE INDEX notification_rules_cell ON notification_rules (event, ifnull(channel_id, 0));
