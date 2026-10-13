---
question: 'Warum sagt mein Browser, die Verbindung sei nicht sicher?'
category: security
tags: [https, http, nicht sicher, zertifikat, ssl, tls, browserwarnung]
order: 20
status: ready
---

Die Box erstellt ihr eigenes Zertifikat, und dein Browser weiß noch nicht, wer es signiert hat. Verschlüsselt ist die Verbindung trotzdem. Fahre zur Seite fort, lade dann unter Einstellungen > **Zertifikate** das Root-Zertifikat der Box herunter und installiere es auf dem Gerät; danach ist die Warnung weg.

Details: [Zertifikate](/de/docs/admin/certificates/).
