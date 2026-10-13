---
question: 'Ist der Datenverkehr zu meiner Box verschlüsselt?'
category: security
tags: [verschlüsselung, tls, https, smb-verschlüsselung, sftp, ftps, webdav]
order: 30
status: ready
---

Ja, bei jedem Dienst außer reinem FTP. Der Web-Desktop und WebDAV nutzen HTTPS, SFTP und FTP mit TLS sind verschlüsselt, und SMB 3 ist immer signiert und verschlüsselt, wenn der Client es verlangt oder ein Administrator es für einen freigegebenen Ordner vorschreibt. Reines FTP ist aus, solange ein Administrator es nicht erlaubt. Nur das Live-System des Installers spricht reines HTTP.

Details: [Sicherheit](/de/docs/admin/security/).
