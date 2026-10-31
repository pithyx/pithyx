-- The kinds of app notifications a user muted (D390): one row per user, app and category of the app's manifest. A
-- muted notification is kept as read, without a toast and without the user's email and webhook channels.
CREATE TABLE notification_mutes (
    user_id INTEGER NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    app_id TEXT NOT NULL REFERENCES apps (id) ON DELETE CASCADE,
    category TEXT NOT NULL CHECK (length(category) BETWEEN 1 AND 32),
    PRIMARY KEY (user_id, app_id, category)
) STRICT;
