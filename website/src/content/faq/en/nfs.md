---
question: 'Does Pithyx support NFS or older SMB versions?'
category: files
tags: [nfs, smb1, smb2, cifs, old devices, scanner, guest access]
order: 70
status: ready
---

No. Pithyx offers SMB 3, SFTP, WebDAV and FTP, with no NFS and no guest access, and older clients that only speak SMB 1 or 2 cannot connect. For devices such as scanners, use FTP (with TLS, or plain FTP if an administrator allows it).

Details: [File services](/docs/admin/file-services/).
