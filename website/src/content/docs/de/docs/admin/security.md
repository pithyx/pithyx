---
title: 'Sicherheit'
description: 'Transport, offene Ports, physischer Zugriff, SSH, Zwei-Faktor-Anmeldung, Passkeys, der Wiederherstellungsschlüssel, Apps mit Root-Zugriff und was die Box verlässt.'
status: ready
audience: admin
sidebar:
  order: 9
---

Pithyx behält alles auf der Box: keine Telemetrie, kein Cloud-Konto, kein Nach-Hause-Telefonieren. Der Web-Desktop spricht HTTPS mit einem Zertifikat der eigenen Zertifizierungsstelle der Box. Diese Seite behandelt Transport, offene Ports, physischen Zugriff, SSH, Zwei-Faktor-Anmeldung, Passkeys, den Wiederherstellungsschlüssel, Apps mit Root-Zugriff und die wenigen Verbindungen, die die Box von sich aus aufbaut.

## HTTPS und Zertifikate

Die Box liefert den Desktop und WebDAV unter `https://<box-adresse>:7484` aus, über TLS 1.2 oder 1.3 mit HTTP/2. Die Ports 80 und 443 leiten dorthin um, außer für die Domains des [Reverse Proxys](/de/docs/admin/reverse-proxy/), und eine Anfrage mit reinem `http://` auf 7484 wird auf `https://` am selben Port umgeleitet. Die Box ist ihre eigene Zertifizierungsstelle, deshalb warnt ein Browser vor ihrem Zertifikat, bis das Gerät dem Root-Zertifikat der Box vertraut: Lade es unter Einstellungen > **Zertifikate** herunter und installiere es einmal pro Gerät (siehe [Zertifikate](/de/docs/admin/certificates/)). FTP über TLS zeigt dasselbe Zertifikat.

Die Zertifizierungsstelle darf nur lokale Namen und private Adressen signieren, außer ein Administrator legt eine ohne diese Grenze an. Ein Gerät, das ihr vertraut, akzeptiert von der Box also nie ein Zertifikat für eine öffentliche Website. Die Box sendet keinen HSTS-Header: Browser lassen niemanden an einer Zertifikatswarnung vorbei, wenn ein Host HSTS nutzt, und das Zertifikat der Box ist so lange nicht vertrauenswürdig, bis ein Gerät ihrem Root-Zertifikat vertraut.

Das Sitzungs-Cookie ist `HttpOnly`, `SameSite=Strict` und `Secure`. Anfragen, die etwas ändern, brauchen den Header `X-Pithyx-Request`. SMB 3 und SFTP verschlüsseln ihren Verkehr selbst. Die einzige Ausnahme ist das Live-System des Installers: Es spricht reines HTTP, weil es noch kein Zertifikat hat und nach keinem Passwort fragt.

## Offene Ports

Pithyx hat eine Firewall, die nur die Weboberfläche und die eingeschalteten Dateidienste hereinlässt, über IPv4 und IPv6: der Web-Desktop und WebDAV auf 7484, 80 und 443 für die Umleitung und den Reverse Proxy, die Ports aktiver Streams des Reverse Proxys, SMB auf 445 (ab dem ersten Start an), SFTP und SSH auf 22, FTP auf 21 mit den passiven Ports 50000 bis 50099. Schaltest du einen Dienst unter Einstellungen > **Dateidienste** aus, ist auch sein Port zu. Alles andere wird verworfen, außer Antworten auf eigene Verbindungen der Box, DHCP-Antworten und den üblichen ICMP-Nachrichten (darunter Ping) sowie denen, die IPv6 braucht. Die Backends der Apps veröffentlichen gar keine Ports.

Mit **Nur private Netze dürfen die Dateidienste nutzen** (Abschnitt **Firewall** auf derselben Seite, standardmäßig aus) antworten SMB, SFTP, SSH und FTP nur Computern in privaten Netzen (10.x, 172.16 bis 31.x, 192.168.x, Link-local und IPv6-ULA); die Weboberfläche bleibt in jedem Fall erreichbar. Lass es aus, wenn du die Box über einen Router aus einem anderen Netz oder über ein VPN mit öffentlichen Adressen erreichst. Lassen sich die Regeln nicht anwenden, ist die Einstellung trotzdem gespeichert, und der Fehler ist [`network.firewall.failed`](/de/docs/reference/errors/#network.firewall.failed). Pithyx bietet keinen eigenen Fernzugriff; lass die Box hinter einem Router und erreiche sie von außen lieber über ein VPN, statt diese Ports weiterzuleiten.

## Physischer Zugriff

Wer die Konsole der Box bedienen kann, vor Ort oder über die Fernwartung (BMC, IPMI), kann root werden: Das Bootmenü hat kein Passwort, und die Systemplatte ist nicht verschlüsselt. Ein Passwort an der Konsole würde also nichts schützen, deshalb fragen das Konsolenmenü und das [Recovery-Menü](/de/docs/install/updates/#das-recovery-menü) nach keinem und lassen dich nur bestätigen, was du tust. An der Konsole gibt es überhaupt keine Anmeldung: tty1 zeigt nur das Konsolenmenü, die anderen Konsolen sind abgeschaltet, und root bleibt gesperrt. Stell die Box dort auf, wo Fremde nicht hinkommen, und sichere ihre Fernwartung.

Jede Box schreibt bei der Installation ihre eigene ZFS-Host-ID, damit zwei vom selben Stick installierte Boxen nie als dasselbe System gelten. Ein Pool von vor einer Neuinstallation gilt deshalb als von einem anderen System benutzt und braucht den erzwungenen Import (siehe [Wiederherstellung](/de/docs/install/recovery/#die-pools-wieder-importieren)).

Ein Pool lässt sich beim Anlegen verschlüsseln ([Verschlüsselung](/de/docs/admin/storage/#verschlüsselung)). Liegt sein Schlüssel im Schlüsseltresor auf der Systemplatte, entsperrt die Box ihn bei jedem Start: Das schützt nur gegen gestohlene Platten, nicht gegen jemanden, der die ganze Box oder ihre Konsole hat. Ein Pool, der nach jedem Start von Hand entsperrt wird, schützt die Daten auch auf einer gestohlenen Box.

## SSH

SSH ist standardmäßig aus. Zwei getrennte Schalter auf der Seite **Dateidienste**:

- **Freigegebene Ordner über SFTP anbieten**: nur Dateiübertragung, keine Shell.
- **SSH-Anmeldungen** mit dem Schalter **Benutzer mit dem Recht zur SSH-Anmeldung mit einer Shell anmelden lassen**. Benutzer mit dem Recht „Über SSH mit einer Shell anmelden“ bekommen eine Shell mit ihren eigenen Rechten, mit Passwort oder öffentlichem Schlüssel, ohne Weiterleitung, Tunnel oder X11. Eine Shell erreicht mit den Rechten des Benutzers das ganze System, daher gib das Recht nur Leuten, die es brauchen.

Das Ausschalten eines der beiden stoppt SSH für die eigenen Konten des Systems nie; Pithyx hält `sshd` nie an. Eine Anmeldung mit Shell begrüßt dich mit dem Pithyx-Logo, der Version und einem Hinweis, dass Änderungen von Hand in der Shell die Box beschädigen oder von Pithyx oder einem Update rückgängig gemacht werden können (vorerst auf Englisch). Die Host-Schlüssel entstehen beim ersten Start und gehören zum Konfigurationsbackup.

Der Administrator, den du im Installer angelegt hast, ist in der Gruppe `sudo` und bekommt mit `sudo` und seinem eigenen Passwort root (er muss sich seit der Installation einmal an der Weboberfläche angemeldet haben, damit das System das Passwort kennt). Andere Administratoren bekommen kein `sudo`. Root selbst bleibt gesperrt; was du damit machst, entscheidest du. `sudo`, `nano`, `htop`, `curl`, `wget` und `git` sind installiert.

## Zwei-Faktor-Anmeldung

Jeder Benutzer kann die Zwei-Faktor-Anmeldung (TOTP) in **Persönliche Einstellungen** unter **Zwei-Faktor-Authentifizierung** einschalten: den QR-Code scannen oder den Schlüssel eingeben, einen Code eingeben, **Einschalten** wählen und die 10 Wiederherstellungscodes aufbewahren, die einmal angezeigt werden; jeder gilt einmal. Die Codes nutzen SHA-1, 6 Stellen und 30 Sekunden. Gib bei der Anmeldung den Code ein oder wähle „Stattdessen einen Wiederherstellungscode verwenden“; die Abfrage ist 5 Minuten gültig. Zum Ausschalten braucht es das Passwort. Nach 5 fehlgeschlagenen Versuchen für einen Benutzer von einer Adresse oder 30 von einer Adresse in 5 Minuten werden weitere Versuche eine Zeit lang abgelehnt. Sitzungen enden nach 7 Tagen ohne Nutzung oder nach insgesamt 30 Tagen.

SMB, SFTP, WebDAV und FTP nutzen nur das Passwort.

## Passkeys

Jeder Benutzer kann in **Persönliche Einstellungen** unter **Passkeys** Passkeys hinzufügen ([Dein Konto](/de/docs/user/account/#passkeys)). Ein Passkey ersetzt bei der Anmeldung am Desktop Passwort und zweiten Faktor; Dateidienste nutzen weiter das Passwort. Pithyx verlangt auffindbare Anmeldedaten mit Benutzerprüfung (PIN, Fingerabdruck oder Gesicht des Geräts) und keine Attestierung. Es fragt nie einen Metadatendienst, also verlässt nichts die Box. Einen hinzuzufügen braucht das aktuelle Passwort; jeder Benutzer hat bis zu 20.

Ein Passkey ist an den Namen gebunden, unter dem die Box geöffnet wurde (die WebAuthn Relying Party). Er funktioniert also nur dort und nur über HTTPS auf einem Gerät, das der Box vertraut; für eine Adresse lehnt die Box Passkeys mit `auth.passkey.unavailable` ab. Jede Anmeldung nutzt eine einmalige Challenge, die 5 Minuten gilt, und prüft den Signaturzähler. Fehlgeschlagene Passkey-Anmeldungen zählen zur Grenze von 30 pro Adresse in 5 Minuten. Ein deaktiviertes Konto oder eines ohne das Recht zur Desktop-Anmeldung wird nach der Prüfung des Passkeys abgelehnt, wie beim Passwort. Das Zurücksetzen eines Passworts mit dem Wiederherstellungsschlüssel entfernt die Passkeys dieses Administrators. Passkeys gehören zum Konfigurationsbackup (Teil **Benutzer und Gruppen**).

## Der Wiederherstellungsschlüssel

Die Ersteinrichtung zeigt den Wiederherstellungsschlüssel einmal an (`PTHX-` gefolgt von 26 Zeichen). Er ist der einzige Weg, das Administratorpasswort zurückzusetzen. Wähle dazu auf der Anmeldeseite „Passwort vergessen?“ und gib dann den Schlüssel, den Benutzernamen eines Administrators und ein neues Passwort ein. Das meldet diesen Benutzer überall ab, schaltet seine Zwei-Faktor-Anmeldung aus, entfernt seine Passkeys und stellt einen neuen Wiederherstellungsschlüssel aus; der alte wird ungültig. Es gibt einen Schlüssel für die ganze Box, und er gehört zum Konfigurationsbackup. Ohne den Schlüssel und das Passwort muss das System neu installiert werden. Andere Benutzer bitten einen Administrator um ein neues Passwort.

## Apps mit Root-Zugriff

Manche Apps brauchen ein Recht, das root auf der Box ist, heute den ungefilterten Containerdienst `docker.socket.raw` (etwa eine Containerverwaltung). Pithyx lehnt sie nicht ab; ein solches Recht zu geben, verlangt im Installationsdialog einen eigenen roten Schritt, den eingetippten Namen der App und dein Passwort ([Sonderrechte](/de/docs/admin/apps-and-store/#sonderrechte)). Danach gilt:

- Die App trägt im Store und auf ihrer Seite das Abzeichen **Root-Zugriff**.
- **Einstellungen > Sicherheit** listet jede App mit Root-Zugriff und ihre Rechte. **Root-Zugriff entziehen** nimmt das Recht sofort weg, ohne Passwort; der Hintergrunddienst der App startet ohne es neu. Gibst du es wieder, verlangt Pithyx die volle Zustimmung.
- Jeder andere Administrator bekommt die Benachrichtigung "<App> hat jetzt Root-Zugriff", auch per E-Mail oder Webhook, wo eingerichtet.
- Die App aktualisiert sich nie von selbst, egal was die Update-Regel sagt. Lies die Versionshinweise und installiere ihr Update mit einem Klick auf der Seite **Updates** des Stores.

## Was die Box verlässt

Die Box schickt keine Daten an das Pithyx-Projekt. Das sind die ausgehenden Verbindungen, die Pithyx selbst aufbaut:

- **Systemupdates**: einmal am Tag zu einer Zeit, die jede Box zufällig wählt (abschaltbar mit **Täglich nach Updates suchen** unter Einstellungen > **Updates**), und bei **Jetzt prüfen** liest die Box die Kanaldatei ihrer Update-Quelle (standardmäßig die Releases des Projekts auf GitHub) und die Paketlisten von Debian von `deb.debian.org`. Ein Update, das du installierst, lädt seine Datei von derselben Quelle. Siehe [Systemupdates](/de/docs/install/updates/).
- **App-Kataloge**: Git über HTTPS oder SSH zu jedem aktivierten Katalog-Repository (der offizielle und der Community-Katalog sind an und werden standardmäßig von GitHub abgerufen), einmal täglich zur eingestellten Zeit mit einer zufälligen Verzögerung von bis zu einer Stunde, wenn du einen Katalog hinzufügst, und bei **Jetzt prüfen**. Schalte einen Katalog aus, um das zu beenden.
- **Container-Images** von Katalog-Apps aus der Registry, die der Katalog nennt, außer das Paket bringt sie mit.
- **Zeit**: Ist **Zeitserver** unter Einstellungen > **Regionale Einstellungen** leer, nutzt die Box die Debian-Vorgaben über systemd-timesyncd, das öffentliche Zeitserver kontaktiert.
- **Benachrichtigungskanäle**: der E-Mail-Server und die Webhook-Adressen der [Benachrichtigungskanäle](/de/docs/admin/notifications/), wenn es etwas zu senden gibt oder bei **Test senden**.
- **Let's Encrypt**: die ACME-Zertifizierungsstelle von Reverse-Proxy-Hosts mit einem Zertifikat von ihr, um es zu holen und zu erneuern ([Reverse Proxy](/de/docs/admin/reverse-proxy/#lets-encrypt)).
- **USV**: der NUT-Server einer USV im Netzwerk, alle 5 Sekunden ([USV](/de/docs/admin/ups/)).
- **Apps**, denen du die Berechtigung „Internet“ erlaubst, bekommen ein zweites Netzwerk mit ausgehendem Zugriff ins Internet; andere Apps haben keines. Geräte in deinem lokalen Netzwerk erreichen nur Apps mit der Berechtigung „Lokales Netzwerk“.

Logs bleiben auf der Box unter `/var/log/pithyx/`. Geheimnisse werden in Logs maskiert. Passwörter werden als Argon2id-Hashes gespeichert.
