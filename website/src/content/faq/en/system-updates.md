---
question: 'How do I update Pithyx?'
category: updates
tags: [update, upgrade, new version, system update, apt, debian, rollback]
order: 10
status: draft
---

In Settings > **Updates**. The box checks its channel once a day and notifies the administrators; it never installs by itself. **Install {version}** takes a snapshot of the system, installs the update and restarts, and goes back to the snapshot by itself if the new version fails its check. Without internet, upload a `.pithyxupdate` file instead. Pools and their files are not touched.

Details: [System updates](/docs/install/updates/).
