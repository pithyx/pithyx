---
question: 'Welche Ports nutzt Pithyx?'
category: network
tags: [ports, firewall, '7484', '445', '22', '21', portweiterleitung, router]
order: 30
status: draft
---

Web-Desktop und WebDAV nutzen Port 7484 (unverschlüsseltes HTTP), Port 80 leitet dorthin weiter. SMB nutzt 445, SFTP 22 und FTP 21 mit den passiven Datenports 50000 bis 50099. Einen eigenen Fernzugriff bietet Pithyx nicht; weil der Desktop noch nicht verschlüsselt ist, leite diese Ports nicht von deinem Router ins Internet weiter.

Details: [Dateidienste](/de/docs/admin/file-services/).
