---
question: 'Why does my browser say the connection is not secure?'
category: security
tags: [https, http, not secure, certificate, ssl, tls, browser warning]
order: 20
status: ready
---

The box makes its own certificate, and your browser does not know who signed it yet. The connection is encrypted all the same. Continue to the page, then download the box's root in Settings > **Certificates** and install it on the device; after that the warning is gone.

Details: [Certificates](/docs/admin/certificates/).
