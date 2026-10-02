-- The look of the desktop that follows a user to every device (D281): accent color and wallpaper. The theme stays a
-- choice of each device. A user without a row sees the defaults (petrol, the Pithyx boxes).
CREATE TABLE user_appearance (
    user_id INTEGER PRIMARY KEY REFERENCES users (id) ON DELETE CASCADE,
    accent TEXT NOT NULL CHECK (accent IN ('petrol', 'ocean', 'indigo', 'plum', 'moss', 'graphite')),
    wallpaper TEXT NOT NULL CHECK (wallpaper IN ('boxes', 'plain')),
    updated_at TEXT NOT NULL
) STRICT;

-- The regional settings of the box (D282): the system language and the region whose formats every user sees. One
-- row at most; without it the language is English and the formats follow each user's language.
CREATE TABLE system_region (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    language TEXT NOT NULL,
    -- ISO 3166 country code such as DE or US, or null to follow the language.
    region TEXT,
    updated_at TEXT NOT NULL
) STRICT;
