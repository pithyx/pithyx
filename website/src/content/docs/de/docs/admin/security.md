---
title: 'Sicherheit'
description: 'Transport, offene Ports, physischer Zugriff, SSH, Zwei-Faktor-Anmeldung, der Wiederherstellungsschlüssel und was die Box verlässt.'
status: draft
audience: admin
sidebar:
  order: 9
---

Pithyx behält alles auf der Box: keine Telemetrie, kein Cloud-Konto, kein Nach-Hause-Telefonieren. Eines solltest du vor der Nutzung wissen: Der Web-Desktop wird heute über reines HTTP ausgeliefert. Diese Seite behandelt Transport, offene Ports, physischen Zugriff, SSH, Zwei-Faktor-Anmeldung, den Wiederherstellungsschlüssel und die wenigen Verbindungen, die die Box von sich aus aufbaut.

## HTTPS und Zertifikate

:::caution[Reines HTTP]
Die Box liefert den Desktop unter `http://<box-adresse>:7484` aus, und Port 80 leitet dorthin um. HTTPS ist nicht eingebaut, daher laufen das Anmeldepasswort, das Sitzungs-Cookie und die Basic-Authentifizierung von WebDAV unverschlüsselt durchs Netz. Nutze die Box nur in einem Netzwerk, dem du vertraust.
:::

Das Sitzungs-Cookie ist `HttpOnly` und `SameSite=Strict`, und es bekommt das Flag `Secure` nur, wenn eine Anfrage über HTTPS eintrifft. Anfragen, die etwas ändern, brauchen den Header `X-Pithyx-Request`. Das einzige TLS, das die Box heute hat, ist das selbstsignierte Zertifikat von FTP über TLS (siehe [Dateidienste](/de/docs/admin/file-services/#ftp)). SMB 3 und SFTP verschlüsseln ihren Verkehr selbst.

## Offene Ports

Pithyx hat heute keine Firewall für seine eigenen Dienste. Jeder eingeschaltete Dienst antwortet in jedem Netz, mit dem die Box verbunden ist, über IPv4 und IPv6: der Web-Desktop und WebDAV auf 7484 und 80, SMB auf 445 (ab dem ersten Start an), SFTP und SSH auf 22, FTP auf 21 mit den passiven Ports 50000 bis 50099. Abgeschirmt sind nur die Backends der Apps: Sie veröffentlichen gar keine Ports. Lass die Box hinter einem Router und leite diese Ports nicht ins Internet weiter.

## Physischer Zugriff

Wer die Konsole der Box bedienen kann, vor Ort oder über die Fernwartung (BMC, IPMI), kann root werden: Das Bootmenü hat kein Passwort, und die Systemplatte ist nicht verschlüsselt. Ein Passwort an der Konsole würde also nichts schützen, deshalb fragen das Konsolenmenü und das [Recovery-Menü](/de/docs/install/updates/#das-recovery-menü) nach keinem und lassen dich nur bestätigen, was du tust. Stell die Box dort auf, wo Fremde nicht hinkommen, und sichere ihre Fernwartung.

Pool-Verschlüsselung gibt es in dieser Version nicht; der Speicher-Assistent sagt, sie kommt mit dem Schlüsseltresor. Ein Schlüssel in einem Tresor auf der Systemplatte schützt nur gegen gestohlene Platten, nicht gegen jemanden, der die ganze Box oder ihre Konsole hat.

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

Die Box schickt keine Daten an das Pithyx-Projekt. Das sind die ausgehenden Verbindungen, die Pithyx selbst aufbaut:

- **Systemupdates**: einmal am Tag zu einer Zeit, die jede Box zufällig wählt (abschaltbar mit **Täglich nach Updates suchen** unter Einstellungen > **Updates**), und bei **Jetzt prüfen** liest die Box die Kanaldatei ihrer Update-Quelle (standardmäßig die Releases des Projekts auf GitHub) und die Paketlisten von Debian von `deb.debian.org`. Ein Update, das du installierst, lädt seine Datei von derselben Quelle. Siehe [Systemupdates](/de/docs/install/updates/).
- **App-Kataloge**: Git über HTTPS oder SSH zu jedem aktivierten Katalog-Repository (der offizielle und der Community-Katalog sind an und werden standardmäßig von GitHub abgerufen), einmal täglich zur eingestellten Zeit mit einer zufälligen Verzögerung von bis zu einer Stunde, wenn du einen Katalog hinzufügst, und bei **Jetzt prüfen**. Schalte einen Katalog aus, um das zu beenden.
- **Container-Images** von Katalog-Apps aus der Registry, die der Katalog nennt, außer das Paket bringt sie mit.
- **Zeit**: Ist **Zeitserver** unter Einstellungen > **Regionale Einstellungen** leer, nutzt die Box die Debian-Vorgaben über systemd-timesyncd, das öffentliche Zeitserver kontaktiert.
- **Apps**, denen du die Berechtigung „Internet“ erlaubst, bekommen ein zweites Netzwerk mit ausgehendem Zugriff; andere Apps haben keines.

Logs bleiben auf der Box unter `/var/log/pithyx/`. Geheimnisse werden in Logs maskiert. Passwörter werden als Argon2id-Hashes gespeichert.
