---
question: 'Can I open an encrypted pool without Pithyx?'
category: storage
tags: [encryption, zfs, recovery, other linux, hex key, broken box]
order: 32
status: ready
---

Yes, on any Linux with OpenZFS 2.x. The key sheet you downloaded or printed carries the key also in hex and the commands: `zpool import -f -N <pool>`, `zfs load-key <pool>/pithyx` (paste the hex key) and `zfs mount -a`.

Details: [Opening the pool without Pithyx](/docs/admin/encryption-emergencies/#opening-the-pool-without-pithyx).
