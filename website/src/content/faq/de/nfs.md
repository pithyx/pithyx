---
question: 'Unterstützt Pithyx NFS oder ältere SMB-Versionen?'
category: files
tags: [nfs, smb1, smb2, cifs, alte geräte, scanner, gastzugang]
order: 70
status: ready
---

Nein. Pithyx bietet SMB 3, SFTP, WebDAV und FTP an, ohne NFS und ohne Gastzugang, und ältere Clients, die nur SMB 1 oder 2 sprechen, können sich nicht verbinden. Für Geräte wie Scanner nimm FTP (mit TLS oder, wenn ein Administrator es erlaubt, unverschlüsselt).

Details: [Dateidienste](/de/docs/admin/file-services/).
