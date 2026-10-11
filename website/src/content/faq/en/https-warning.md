---
question: 'Why does my browser say the connection is not secure?'
category: security
tags: [https, http, not secure, certificate, ssl, tls, browser warning]
order: 20
status: ready
---

The box serves the desktop over plain HTTP on port 7484 today; HTTPS is not built yet. Your password and session cookie therefore travel unencrypted, so use the box only on a network you trust and do not expose it to the internet.

Details: [HTTPS and certificates](/docs/admin/security/#https-and-certificates).
