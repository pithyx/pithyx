---
question: 'Kann ich die Box hinter einen Reverse Proxy stellen?'
category: network
tags: [reverse proxy, proxy, https, zertifikat, domain, websocket]
order: 40
status: ready
---

Ja. Lass den Proxy auf `https://<box-adresse>:7484` zeigen, ihm dem Root-Zertifikat der Box vertrauen (Einstellungen > **Zertifikate**) oder die Zertifikatsprüfung für dieses Backend auslassen, und WebSocket-Upgrades durchreichen. Die Box spricht nur HTTPS; die Verbindung vom Proxy kostet im lokalen Netz so gut wie nichts.

Details: [Zertifikate](/de/docs/admin/certificates/#hinter-einem-reverse-proxy).
