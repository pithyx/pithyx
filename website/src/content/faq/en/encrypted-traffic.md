---
question: 'Is the traffic to my box encrypted?'
category: security
tags: [encryption, tls, https, smb encryption, sftp, ftps, webdav]
order: 30
status: ready
---

It depends on the service. SFTP and FTP with TLS are encrypted, and SMB 3 is always signed, and encrypted when the client asks for it or an administrator requires it for a shared folder. The web desktop and WebDAV use plain HTTP today and are not encrypted.

Details: [Security](/docs/admin/security/).
