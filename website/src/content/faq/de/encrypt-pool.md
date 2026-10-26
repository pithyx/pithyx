---
question: 'Kann ich einen Pool verschlüsseln?'
category: storage
tags: [verschlüsselung, verschlüsselter pool, zfs-verschlüsselung, schlüssel, gestohlene platte]
order: 30
status: ready
---

Ja, beim Anlegen: Wähle im Pool-Assistenten **Mit einem Schlüssel verschlüsseln**. Nachträglich lässt sich ein Pool nicht verschlüsseln. Die Box zeigt den Schlüssel des Pools einmal; bewahre ihn getrennt von der Box auf, denn ohne ihn sind die Daten verloren. Die Box kann den Schlüssel in ihrem Schlüsseltresor aufbewahren und den Pool bei jedem Start entsperren (das schützt gegen gestohlene Platten), oder der Pool entsperrt sich, sobald ein USB-Stick mit seinem Schlüssel steckt, oder er bleibt nach jedem Start gesperrt, bis ein Administrator den Schlüssel eingibt (beides schützt auch gegen eine gestohlene Box, der Stick nur, solange er nicht bei der Box ist).

Details: [Verschlüsselung](/de/docs/admin/storage/#verschlüsselung).
