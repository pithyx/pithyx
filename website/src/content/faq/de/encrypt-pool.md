---
question: 'Kann ich einen Pool verschlüsseln?'
category: storage
tags: [verschlüsselung, verschlüsselter pool, zfs-verschlüsselung, schlüssel, gestohlene platte]
order: 30
status: ready
---

Ja, beim Anlegen: Wähle im Pool-Assistenten **Mit einem Schlüssel verschlüsseln**. Nachträglich lässt sich ein Pool nicht verschlüsseln. Die Box zeigt den Schlüssel des Pools einmal; bewahre ihn getrennt von der Box auf, denn ohne ihn sind die Daten verloren. Die Box kann den Schlüssel in ihrem Schlüsseltresor aufbewahren und den Pool bei jedem Start entsperren (das schützt gegen gestohlene Platten), oder der Pool bleibt nach jedem Start gesperrt, bis ein Administrator den Schlüssel eingibt (das schützt auch gegen eine gestohlene Box).

Details: [Verschlüsselung](/de/docs/admin/storage/#verschlüsselung).
