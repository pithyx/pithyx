-- Manifest version 2 (D388): the stored manifests of installed apps and of the version before their last update take
-- the shape the box reads now. `permissions` and the backend's `elevated` become one map by name, a device of
-- `optionalDevices` gets `optional: true` there, `arch` names both architectures (the app runs here already) and
-- `source` is the homepage or `private`. A migrated entry has no `reason`, since the package gave none; the Store says
-- so until the app's next update brings its own manifest.
CREATE TEMP TABLE manifest_v2 (
    app_id TEXT NOT NULL,
    kind TEXT NOT NULL CHECK (kind IN ('current', 'previous')),
    manifest TEXT NOT NULL
);

INSERT INTO temp.manifest_v2 (app_id, kind, manifest)
SELECT id, 'current', manifest FROM apps WHERE json_extract(manifest, '$.manifestVersion') = 1;

INSERT INTO temp.manifest_v2 (app_id, kind, manifest)
SELECT id, 'previous', json_extract(previous, '$.manifest')
FROM apps
WHERE previous IS NOT NULL AND json_extract(previous, '$.manifest.manifestVersion') = 1;

UPDATE temp.manifest_v2
SET manifest = json_set(
    json_replace(
        json_remove(manifest, '$.permissions', '$.backend.elevated', '$.backend.optionalDevices'),
        '$."$schema"', replace(json_extract(manifest, '$."$schema"'), 'manifest.v1.schema.json', 'manifest.v2.schema.json')),
    '$.manifestVersion', 2,
    '$.arch', json('["amd64","arm64"]'),
    '$.source', coalesce(json_extract(manifest, '$.homepage'), 'private'),
    '$.permissions', json((
        SELECT json_group_object(name, json(grant))
        FROM (
            SELECT p.value AS name, json_object() AS grant
            FROM json_each(temp.manifest_v2.manifest, '$.permissions') AS p
            UNION ALL
            SELECT e.value AS name,
                CASE
                    WHEN e.value LIKE 'device:%' AND EXISTS (
                        SELECT 1 FROM json_each(temp.manifest_v2.manifest, '$.backend.optionalDevices') AS o
                        WHERE o.value = substr(e.value, length('device:') + 1))
                    THEN json_object('optional', json('true'))
                    ELSE json_object()
                END AS grant
            FROM json_each(temp.manifest_v2.manifest, '$.backend.elevated') AS e
        ))));

UPDATE apps
SET manifest = (SELECT m.manifest FROM temp.manifest_v2 AS m WHERE m.app_id = apps.id AND m.kind = 'current')
WHERE id IN (SELECT app_id FROM temp.manifest_v2 WHERE kind = 'current');

UPDATE apps
SET previous = json_set(previous, '$.manifest', json((SELECT m.manifest FROM temp.manifest_v2 AS m WHERE m.app_id = apps.id AND m.kind = 'previous')))
WHERE id IN (SELECT app_id FROM temp.manifest_v2 WHERE kind = 'previous');

DROP TABLE temp.manifest_v2;
