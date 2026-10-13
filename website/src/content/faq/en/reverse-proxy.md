---
question: 'Can I put the box behind a reverse proxy?'
category: network
tags: [reverse proxy, proxy, https, certificate, domain, websocket]
order: 40
status: ready
---

Yes. Point the proxy at `https://<box-address>:7484`, let it trust the box's root (Settings > **Certificates**) or skip the certificate check for this backend, and pass WebSocket upgrades through. The box speaks only HTTPS; the connection from the proxy costs next to nothing on a local network.

Details: [Certificates](/docs/admin/certificates/#behind-a-reverse-proxy).
