---
question: 'A disk in my pool failed. What do I do?'
category: storage
tags: [disk failed, broken disk, degraded, replace disk, resilver, smart]
order: 50
status: ready
---

Administrators get a notification, and a pool with redundancy keeps working in the state **Degraded**. Replace the disk with **Replace…** in Storage and confirm the new disk's serial number; ZFS then rebuilds the data (resilver). A disk that comes back by itself is rebuilt on its own.

Details: [Storage and pools](/docs/admin/storage/#health-and-warnings).
