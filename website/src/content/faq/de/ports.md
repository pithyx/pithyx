---
question: 'Welche Ports nutzt Pithyx?'
category: network
tags: [ports, firewall, '7484', '445', '22', '21', portweiterleitung, router]
order: 30
status: ready
---

Der Web-Desktop und WebDAV nutzen Port 7484 (HTTPS); die Ports 80 und 443 leiten dorthin um, außer für Domains des eingebauten Reverse Proxys. SMB nutzt 445, SFTP 22 und FTP 21 mit den passiven Datenports 50000 bis 50099. Die Firewall der Box lässt nur die Webports, die eingeschalteten Dateidienste und die Ports der Streams des Reverse Proxys herein, und unter Einstellungen > **Dateidienste** lassen sich die Dateidienste auf private Netze begrenzen. Pithyx bietet keinen eigenen Fernzugriff; erreiche die Box von außen lieber über ein VPN, statt diese Ports in deinem Router weiterzuleiten.

Details: [Dateidienste](/de/docs/admin/file-services/).
