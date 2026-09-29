-- The Disks app became Storage (step 8), which only administrators can open. Administrators' Disks shortcuts now open
-- Storage; other users lose theirs. The version grows so every device picks up the change.

UPDATE desktop_items
SET data = json_set(data, '$.app', 'storage'), version = version + 1
WHERE kind = 'shortcut'
    AND json_extract(data, '$.app') = 'disks'
    AND user_id IN (SELECT id FROM users WHERE is_admin = 1);

DELETE FROM desktop_items WHERE kind = 'shortcut' AND json_extract(data, '$.app') = 'disks';
