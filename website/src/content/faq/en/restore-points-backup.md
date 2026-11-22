---
question: 'Are snapshots and restore points a backup?'
category: storage
tags: [backup, snapshot, restore points, ransomware, offsite, configuration backup]
order: 70
status: ready
---

No. Restore points and snapshots live on the same disks as your files, so they help against accidental deletion and ransomware but not against a lost pool. The configuration backup in Settings holds settings and users, not your files. Keep a copy of your files on another device: the **Backup** app sets up servers reached over SSH, such as a rented storage box, as [backup targets](/docs/admin/backup-targets/); scheduled backups to them follow in a later version.

Details: [Restore points](/docs/admin/shared-folders/#restore-points).
