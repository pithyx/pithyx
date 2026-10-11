---
question: 'Ist der Datenverkehr zu meiner Box verschlüsselt?'
category: security
tags: [verschlüsselung, tls, https, smb-verschlüsselung, sftp, ftps, webdav]
order: 30
status: ready
---

Das hängt vom Dienst ab. SFTP und FTP mit TLS sind verschlüsselt, und SMB 3 ist immer signiert und wird verschlüsselt, wenn der Client es verlangt oder ein Administrator es für einen freigegebenen Ordner erzwingt. Web-Desktop und WebDAV laufen heute über unverschlüsseltes HTTP.

Details: [Sicherheit](/de/docs/admin/security/).
