---
question: 'Can the box make my other services reachable under their own domains?'
category: network
tags:
  [
    reverse proxy,
    proxy host,
    domain,
    subdomain,
    https,
    certificate,
    port forwarding,
    stream,
    tcp,
    udp,
  ]
order: 45
status: draft
---

Yes. Settings > **Reverse proxy** forwards domains on ports 443 and 80 to services in your network or on the box, with a certificate from the box's authority or one you upload, optionally behind Basic Auth or the Pithyx sign-in. Point the domains at the box in your DNS or router first. Streams forward a TCP or UDP port for services that do not speak HTTP. The box itself stays on port 7484.

Details: [Reverse proxy](/docs/admin/reverse-proxy/).
