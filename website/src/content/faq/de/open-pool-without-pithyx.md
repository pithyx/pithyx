---
question: 'Kann ich einen verschlüsselten Pool ohne Pithyx öffnen?'
category: storage
tags: [verschlüsselung, zfs, wiederherstellung, anderes linux, hex-schlüssel, kaputte box]
order: 32
status: ready
---

Ja, auf jedem Linux mit OpenZFS 2.x. Das heruntergeladene oder ausgedruckte Schlüsselblatt enthält den Schlüssel auch in Hex und die Befehle: `zpool import -f -N <pool>`, `zfs load-key <pool>/pithyx` (den Hex-Schlüssel einfügen) und `zfs mount -a`.

Details: [Den Pool ohne Pithyx öffnen](/de/docs/admin/encryption-emergencies/#den-pool-ohne-pithyx-öffnen).
