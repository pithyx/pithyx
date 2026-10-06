-- The system apps take ids of D302 in the reserved namespace org.pithyx (A3): files becomes org.pithyx.files, and so
-- on. Everything stored with an app id follows: grants (app:files becomes app:org.pithyx.files), window preferences,
-- desktop shortcuts, the windows of saved layouts and the notifications apps sent. Log entries keep their source
-- (app/files); the Log Center treats both names as one app. Shortcuts and layouts get a new version, so every device
-- picks up the change. Where a row already exists under the new id, it wins and the old one goes.

CREATE TEMP TABLE system_app_ids (old TEXT PRIMARY KEY, new TEXT NOT NULL) STRICT;

INSERT INTO temp.system_app_ids (old, new) VALUES
    ('files', 'org.pithyx.files'),
    ('viewer', 'org.pithyx.viewer'),
    ('storage', 'org.pithyx.storage'),
    ('settings', 'org.pithyx.settings'),
    ('monitor', 'org.pithyx.monitor'),
    ('logs', 'org.pithyx.logs'),
    ('about', 'org.pithyx.about'),
    ('demo', 'org.pithyx.demo');

UPDATE OR IGNORE grants
SET resource = 'app:' || (SELECT new FROM temp.system_app_ids WHERE old = substr(grants.resource, 5))
WHERE resource IN (SELECT 'app:' || old FROM temp.system_app_ids);

DELETE FROM grants WHERE resource IN (SELECT 'app:' || old FROM temp.system_app_ids);

UPDATE OR IGNORE window_preferences
SET app_id = (SELECT new FROM temp.system_app_ids WHERE old = window_preferences.app_id)
WHERE app_id IN (SELECT old FROM temp.system_app_ids);

DELETE FROM window_preferences WHERE app_id IN (SELECT old FROM temp.system_app_ids);

UPDATE desktop_items
SET data = json_set(data, '$.app', (SELECT new FROM temp.system_app_ids WHERE old = json_extract(desktop_items.data, '$.app'))),
    version = version + 1
WHERE kind = 'shortcut' AND json_extract(data, '$.app') IN (SELECT old FROM temp.system_app_ids);

-- A layout's windows are rebuilt in their order; only the app of a renamed window changes.
UPDATE desktop_layouts
SET layout = json_set(layout, '$.windows', json((
        SELECT json_group_array(json(
            CASE
                WHEN ids.new IS NOT NULL THEN json_set(w.value, '$.app', ids.new)
                WHEN w.type = 'text' THEN json_quote(w.value)
                ELSE w.value
            END) ORDER BY w.key)
        FROM json_each(desktop_layouts.layout, '$.windows') AS w
        LEFT JOIN temp.system_app_ids AS ids
            ON ids.old = CASE WHEN w.type = 'object' THEN json_extract(w.value, '$.app') END))),
    version = version + 1
WHERE EXISTS (
    SELECT 1
    FROM json_each(desktop_layouts.layout, '$.windows') AS w
    JOIN temp.system_app_ids AS ids ON ids.old = CASE WHEN w.type = 'object' THEN json_extract(w.value, '$.app') END);

UPDATE notifications
SET app = (SELECT new FROM temp.system_app_ids WHERE old = notifications.app)
WHERE app IN (SELECT old FROM temp.system_app_ids);

DROP TABLE temp.system_app_ids;
