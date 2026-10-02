-- Restore points (D283): every shared folder on a dataset gets daily snapshots kept 7 days, on by default. A dataset
-- that already has a daily schedule keeps it as it is, switched off or not.
INSERT INTO snapshot_schedules (dataset, kind, enabled, keep, updated_at)
SELECT dataset, 'daily', 1, 7, strftime('%Y-%m-%dT%H:%M:%fZ', 'now')
FROM shares
WHERE dataset IS NOT NULL
ON CONFLICT (dataset, kind) DO NOTHING;
