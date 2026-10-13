---
question: 'Kann die Box meine anderen Dienste unter eigenen Domains erreichbar machen?'
category: network
tags:
  [
    reverse proxy,
    proxy-host,
    domain,
    subdomain,
    https,
    zertifikat,
    portweiterleitung,
    stream,
    tcp,
    udp,
  ]
order: 45
status: draft
---

Ja. Einstellungen > **Reverse Proxy** leitet Domains auf den Ports 443 und 80 an Dienste in deinem Netz oder auf der Box weiter, mit einem Zertifikat der Zertifizierungsstelle der Box oder einem hochgeladenen, auf Wunsch hinter Basic Auth oder der Pithyx-Anmeldung. Lass die Domains vorher in deinem DNS oder Router auf die Box zeigen. Streams leiten einen TCP- oder UDP-Port für Dienste weiter, die kein HTTP sprechen. Die Box selbst bleibt auf Port 7484.

Details: [Reverse Proxy](/de/docs/admin/reverse-proxy/).
