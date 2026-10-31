---
question: 'Why does my browser say the connection is not secure?'
category: security
tags: [https, http, not secure, certificate, ssl, tls, browser warning]
order: 20
status: ready
---

The box makes its own certificate, and your browser does not know who signed it yet. The connection is encrypted all the same. Continue to the page and sign in: the desktop offers **Trust this device** with the box's root certificate and the steps for your system (administrators also find it in Settings > **Certificates**). Install it on the device and restart the browser; after that the warning is gone and passkeys work.

Details: [Certificates](/docs/admin/certificates/).
