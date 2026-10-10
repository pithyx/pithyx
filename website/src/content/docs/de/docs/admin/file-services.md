---
title: 'Dateidienste'
description: 'SMB, SFTP, WebDAV und FTP ein- oder ausschalten, mit ihren Ports, Standardwerten und Grenzen.'
status: draft
audience: admin
sidebar:
  order: 7
---

Dateidienste lassen andere Computer freigegebene Ordner über das Netzwerk öffnen. Einstellungen > **Dateidienste** hat einen Schalter pro Dienst: SMB, SFTP, WebDAV und FTP. Benutzer melden sich mit ihrem Pithyx-Benutzernamen und Passwort an. Pithyx hat kein NFS und keinen Gastzugang. Bei einer frischen Installation ist SMB an und die anderen Dienste sind aus.

Welcher Benutzer welchen Dienst nutzen darf, wird pro Benutzer oder Gruppe festgelegt (siehe [Berechtigungen](/de/docs/admin/permissions/)); welche Ordner ein Dienst anbietet, wird pro Ordner festgelegt ([Freigegebene Ordner](/de/docs/admin/shared-folders/)). Ein Benutzer, dessen Passwort Pithyx für SMB und SFTP noch nicht kennt, muss sich einmal im Web anmelden, oder ein Administrator setzt ein neues Passwort.

## SMB

Schalte **Freigegebene Ordner über SMB anbieten** ein; die Standard-Arbeitsgruppe ist `WORKGROUP` (1 bis 15 Zeichen: Buchstaben, Ziffern, Punkt, Bindestrich, Unterstrich, beginnend mit einem Buchstaben oder einer Ziffer). SMB stellt Windows, macOS und Linux Netzlaufwerke auf Port 445 bereit. Es werden nur signierte Verbindungen aktueller Systeme (SMB 3) akzeptiert, verschlüsselt, sobald der Client es kann, und immer verschlüsselt bei freigegebenen Ordnern mit **Verschlüsselung über SMB verlangen** (siehe [Freigegebene Ordner](/de/docs/admin/shared-folders/#verschlüsselung-über-smb)); Gäste werden abgelehnt, und NetBIOS ist aus. Pithyx schreibt `/etc/samba/smb.conf` bei jeder Änderung selbst; eigene Samba-Einstellungen gehören in `/etc/samba/pithyx.local.conf`, die Pithyx einbindet und nie beschreibt. Die Datei wird mit `testparm` geprüft, bevor Samba neu lädt.

Verbinde dich im Windows-Explorer mit `\\<adresse>\<freigabe>`, unter macOS oder Linux mit `smb://<adresse>`. Die Box macht sich im Netzwerk nicht bekannt (kein WS-Discovery, kein mDNS), daher verbinden sich Clients über die Adresse. Gelöschtes geht in den Papierkorb, und die täglichen Wiederherstellungspunkte erscheinen in Windows als „Vorgängerversionen“.

## SFTP

Schalte **Freigegebene Ordner über SFTP anbieten** ein. Es ist verschlüsselte Dateiübertragung über SSH, zum Beispiel mit FileZilla, WinSCP oder dem Befehl `sftp`: `sftp://<adresse>`. Benutzer sehen nur ihre freigegebenen Ordner und bekommen keine Shell. SFTP nutzt das `sshd` des Systems über eine Drop-in-Datei, nur mit Passwort-Anmeldung. Das Ausschalten von SFTP hindert nur Pithyx-Benutzer daran, hereinzukommen; SSH für andere Konten wird nicht gestoppt. Das Einschalten führt `systemctl enable --now ssh.service` aus. Die sshd-Konfiguration wird mit `sshd -t` geprüft und zurückgesetzt, wenn sie abgelehnt wird. SSH mit einer Shell ist ein eigener Schalter, siehe [Sicherheit](/de/docs/admin/security/#ssh).

## WebDAV

Wird vom Daemon selbst unter `/webdav/` auf der Webadresse der Box angeboten (`http://<box-adresse>:7484/webdav/`), mit Basic-Authentifizierung über Pithyx-Name und -Passwort. Die Wurzel listet die freigegebenen Ordner, die der Benutzer nutzen darf; Gelöschtes geht in den Papierkorb. Ein Benutzer braucht das WebDAV-Recht und den Ordnerschalter **Über WebDAV anbieten**.

:::caution[Noch keine Verschlüsselung]
Die Box liefert heute reines HTTP aus, daher läuft das WebDAV-Passwort unverschlüsselt durchs Netz. Nutze WebDAV nur in einem Netzwerk, dem du vertraust. Siehe [Sicherheit](/de/docs/admin/security/#https-und-zertifikate).
:::

## FTP

Für Geräte und Programme, die weder SFTP noch WebDAV kennen, etwa Scanner. **Freigegebene Ordner über FTP mit TLS anbieten** nutzt explizites FTPS (`AUTH TLS`); die Box erzeugt ein selbstsigniertes Zertifikat, und die Seite zeigt dessen SHA-256-Fingerabdruck. **Auch unverschlüsseltes FTP erlauben** ist standardmäßig aus und trägt eine Warnung; wird es wieder ausgeschaltet, enden unverschlüsselte Sitzungen.

- Steuerport 21, passive Datenports 50000 bis 50099. Nur der passive Modus funktioniert (`PORT` und `EPRT` werden mit „502 Only passive mode“ beantwortet).
- Leerlauf-Zeitlimit 10 Minuten, Daten-Zeitlimit 30 Sekunden.
- Fehlgeschlagene Anmeldungen sind auf 5 pro Benutzer und 30 pro Adresse in 5 Minuten begrenzt.

## Grenzen

- Nur SMB 3; ältere Clients können sich nicht verbinden.
- SFTP akzeptiert nur Passwörter.
- Rechte lassen sich nicht von einem Client aus ändern (SMB-ACL-Änderungen werden ignoriert).
- Anmeldungen über Protokolle nutzen keine Zwei-Faktor-Anmeldung.
- Die Box wird im Netzwerk nicht bekannt gemacht (kein WS-Discovery, kein mDNS).
