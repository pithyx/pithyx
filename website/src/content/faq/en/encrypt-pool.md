---
question: 'Can I encrypt a pool?'
category: storage
tags: [encryption, encrypted pool, zfs encryption, key, stolen disk]
order: 30
status: ready
---

Yes, when you create it: choose **Encrypt with a key** in the pool wizard. A pool cannot be encrypted afterwards. The box shows the pool's key once; store it away from the box, because without it the data is lost. The box can keep the key in its key vault and unlock the pool at every start (this protects against stolen disks), or the pool unlocks whenever a USB stick with its key is plugged in, or it stays locked after every start until an administrator types the key (both also protect against a stolen box, the stick only while it is not with the box).

Details: [Encryption](/docs/admin/storage/#encryption).
