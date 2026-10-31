---
question: 'Warum sagt mein Browser, die Verbindung sei nicht sicher?'
category: security
tags: [https, http, nicht sicher, zertifikat, ssl, tls, browserwarnung]
order: 20
status: ready
---

Die Box erstellt ihr eigenes Zertifikat, und dein Browser weiß noch nicht, wer es signiert hat. Verschlüsselt ist die Verbindung trotzdem. Fahre zur Seite fort und melde dich an: Der Desktop bietet **Diesem Gerät vertrauen** mit dem Root-Zertifikat der Box und den Schritten für dein System an (Administratoren finden es auch unter Einstellungen > **Zertifikate**). Installiere es auf dem Gerät und starte den Browser neu; danach ist die Warnung weg und Passkeys funktionieren.

Details: [Zertifikate](/de/docs/admin/certificates/).
