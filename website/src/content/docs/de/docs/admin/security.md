---
title: 'Sicherheit'
description: 'Transport, SSH, Zwei-Faktor-Anmeldung, der Wiederherstellungsschlüssel und was die Box verlässt.'
status: draft
audience: admin
sidebar:
  order: 9
---

Pithyx behält alles auf der Box: keine Telemetrie, kein Cloud-Konto, kein Nach-Hause-Telefonieren. Eines solltest du vor der Nutzung wissen: Der Web-Desktop wird heute über reines HTTP ausgeliefert. Diese Seite behandelt Transport, SSH, Zwei-Faktor-Anmeldung, den Wiederherstellungsschlüssel und die wenigen Verbindungen, die die Box von sich aus aufbaut.

## HTTPS und Zertifikate

:::caution[Reines HTTP]
Die Box liefert den Desktop unter `http://<box-adresse>:7484` aus, und Port 80 leitet dorthin um. HTTPS ist nicht eingebaut, daher laufen das Anmeldepasswort, das Sitzungs-Cookie und die Basic-Authentifizierung von WebDAV unverschlüsselt durchs Netz. Nutze die Box nur in einem Netzwerk, dem du vertraust.
:::

Das Sitzungs-Cookie ist `HttpOnly` und `SameSite=Strict`, und es bekommt das Flag `Secure` nur, wenn eine Anfrage über HTTPS eintrifft. Anfragen, die etwas ändern, brauchen den Header `X-Pithyx-Request`. Das einzige TLS, das die Box heute hat, ist das selbstsignierte Zertifikat von FTP über TLS (siehe [Dateidienste](/de/docs/admin/file-services/#ftp)). SMB 3 und SFTP verschlüsseln ihren Verkehr selbst.

## SSH

SSH ist standardmäßig aus. Zwei getrennte Schalter auf der Seite **Dateidienste**:

- **Freigegebene Ordner über SFTP anbieten**: nur Dateiübertragung, keine Shell.
- **SSH-Anmeldungen** mit dem Schalter **Benutzer mit dem Recht zur SSH-Anmeldung mit einer Shell anmelden lassen**. Benutzer mit dem Recht „Über SSH mit einer Shell anmelden“ bekommen eine Shell mit ihren eigenen Rechten, mit Passwort oder öffentlichem Schlüssel, ohne Weiterleitung, Tunnel oder X11. Eine Shell erreicht mit den Rechten des Benutzers das ganze System, daher gib das Recht nur Leuten, die es brauchen.

Das Ausschalten eines der beiden stoppt SSH für die eigenen Konten des Systems nie; Pithyx hält `sshd` nie an. Die Host-Schlüssel entstehen beim ersten Start und gehören zum Konfigurationsbackup.

## Zwei-Faktor-Anmeldung

Jeder Benutzer kann die Zwei-Faktor-Anmeldung (TOTP) im Kontomenü unter **Zwei-Faktor-Authentifizierung** einschalten: den QR-Code scannen oder den Schlüssel eingeben, einen Code eingeben, **Einschalten** wählen und die 10 Wiederherstellungscodes aufbewahren, die einmal angezeigt werden; jeder gilt einmal. Die Codes nutzen SHA-1, 6 Stellen und 30 Sekunden. Gib bei der Anmeldung den Code ein oder wähle „Stattdessen einen Wiederherstellungscode verwenden“; die Abfrage ist 5 Minuten gültig. Zum Ausschalten braucht es das Passwort. Nach 5 fehlgeschlagenen Versuchen pro Benutzer oder 30 pro Adresse in 5 Minuten werden weitere Versuche eine Zeit lang abgelehnt. Sitzungen enden nach 7 Tagen ohne Nutzung oder nach insgesamt 30 Tagen.

SMB, SFTP, WebDAV und FTP nutzen nur das Passwort.

## Der Wiederherstellungsschlüssel

Die Ersteinrichtung zeigt den Wiederherstellungsschlüssel einmal an (`PTHX-` gefolgt von 26 Zeichen). Er ist der einzige Weg, das Administratorpasswort zurückzusetzen. Wähle dazu auf der Anmeldeseite „Passwort vergessen?“ und gib dann den Schlüssel, den Benutzernamen eines Administrators und ein neues Passwort ein. Das meldet diesen Benutzer überall ab, schaltet seine Zwei-Faktor-Anmeldung aus und stellt einen neuen Wiederherstellungsschlüssel aus; der alte wird ungültig. Es gibt einen Schlüssel für die ganze Box, und er gehört zum Konfigurationsbackup. Ohne den Schlüssel und das Passwort muss das System neu installiert werden. Andere Benutzer bitten einen Administrator um ein neues Passwort.

## Was die Box verlässt

Die Box schickt nichts an das Pithyx-Projekt. Das sind die ausgehenden Verbindungen, die Pithyx selbst aufbaut:

- **App-Kataloge**: Git über HTTPS oder SSH zu jedem aktivierten Katalog-Repository (der offizielle Katalog ist an und wird standardmäßig von GitHub abgerufen; der Community-Katalog ist aus), einmal täglich zur eingestellten Zeit mit einer zufälligen Verzögerung von bis zu einer Stunde, wenn du einen Katalog hinzufügst, und bei **Jetzt prüfen**. Schalte einen Katalog aus, um das zu beenden.
- **Container-Images** von Katalog-Apps aus der Registry, die der Katalog nennt, außer das Paket bringt sie mit.
- **Zeit**: Ist **Zeitserver** unter Einstellungen > **Regionale Einstellungen** leer, nutzt die Box die Debian-Vorgaben über systemd-timesyncd, das öffentliche Zeitserver kontaktiert.
- **Apps**, denen du die Berechtigung „Internet“ erlaubst, bekommen ein zweites Netzwerk mit ausgehendem Zugriff; andere Apps haben keines.

Logs bleiben auf der Box unter `/var/log/pithyx/`. Geheimnisse werden in Logs maskiert. Passwörter werden als Argon2id-Hashes gespeichert.
