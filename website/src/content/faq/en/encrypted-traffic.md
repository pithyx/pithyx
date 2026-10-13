---
question: 'Is the traffic to my box encrypted?'
category: security
tags: [encryption, tls, https, smb encryption, sftp, ftps, webdav]
order: 30
status: ready
---

Yes, for every service but plain FTP. The web desktop and WebDAV use HTTPS, SFTP and FTP with TLS are encrypted, and SMB 3 is always signed, and encrypted when the client asks for it or an administrator requires it for a shared folder. Plain FTP is off unless an administrator allows it. Only the live system of the installer speaks plain HTTP.

Details: [Security](/docs/admin/security/).
