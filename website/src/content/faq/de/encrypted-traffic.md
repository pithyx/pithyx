---
question: 'Ist der Datenverkehr zu meiner Box verschlüsselt?'
category: security
tags: [verschlüsselung, tls, https, smb-verschlüsselung, sftp, ftps, webdav]
order: 30
status: draft
---

Das hängt vom Dienst ab. SFTP und FTP mit TLS sind verschlüsselt, und SMB 3 ist immer signiert und verschlüsselt, wenn der Client es kann (ein Administrator kann es pro freigegebenem Ordner erzwingen). Web-Desktop und WebDAV laufen heute über unverschlüsseltes HTTP.

Details: [Sicherheit](/de/docs/admin/security/).
