---
question: 'Does Pithyx wake my sleeping disks?'
category: storage
tags: [spindown, standby, sleep, noise, power, hdd, wake up]
order: 60
status: ready
---

Pithyx avoids it: SMART values are read only from awake disks, and the Resource Monitor uses the kernel's counters. Some actions do wake disks: a scrub, **Scan disks** when importing, and the optional log archive about once a day. Pithyx has no setting that sends disks to sleep.

Details: [Health and warnings](/docs/admin/storage/#health-and-warnings).
