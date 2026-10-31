---
title: 'Reverse Proxy'
description: 'Proxy-Hosts und Weiterleitungs-Hosts auf den Ports 443 und 80 nach Domain, mit Zertifikaten, Zugriffsregeln und der Pithyx-Anmeldung, sowie TCP- und UDP-Streams.'
status: ready
audience: admin
since: '1.0.0'
sidebar:
  order: 10
---

Der eingebaute Reverse Proxy macht andere Dienste unter eigenen Domains auf den Ports 443 und 80 erreichbar: Eine Anfrage für `media.home.arpa` geht an den Dienst dahinter, mit einem Zertifikat für diesen Namen. Die Box selbst bleibt auf Port 7484; jeder Name, den du nicht eingerichtet hast, geht wie bisher dorthin (oder bekommt 404, wenn du das wählst). Streams leiten einen TCP- oder UDP-Port der Box an einen anderen Host weiter. Alles liegt unter Einstellungen > **Reverse Proxy** (nur Administratoren), gilt ab der nächsten Anfrage ohne Neustart und ist Teil des Konfigurations-Backups.

## Bevor du anfängst

Lass jede Domain in deinem DNS-Server oder Router auf die Box zeigen (ein A- oder AAAA-Eintrag mit der Adresse der Box, oder ein Platzhalter-Eintrag für `*.apps.home.arpa`). Der Proxy beantwortet nur Namen, die ihn erreichen. Ohne HTTPS auf der Box (Entwicklung oder Simulation) antwortet er nur mit einfachem HTTP, und ein Entwicklungs-Daemon lauscht auf den Proxy-Ports nur, wenn `PITHYX_DEV_PROXY_PORTS` sie nennt.

## Proxy-Hosts

**Proxy-Host hinzufügen** öffnet die Einstellungen eines Hosts:

- **Domains**: bis zu 50 Namen, getrennt durch Kommas oder Leerzeichen. Ein führendes `*.` deckt eine beliebige Ebene davor ab (`*.apps.home.arpa` beantwortet `photos.apps.home.arpa`, nicht `a.b.apps.home.arpa`); ein genauer Name gewinnt gegen einen Platzhalter. Ein Name gehört nur zu einem Host ([`proxy.domain.taken`](/de/docs/reference/errors/#proxy.domain.taken)). Ein ausgeschalteter Host beantwortet nichts; seine Namen gelten als unbekannt.
- **Ziel**: Protokoll, Host und Port des Dienstes. Bei `https` wählst du, wie sein Zertifikat geprüft wird: gegen die vertrauenswürdigen Stellen des Systems, gegen die eigene Zertifizierungsstelle der Box (Geräte mit einem Zertifikat aus Einstellungen > **Zertifikate**) oder gar nicht, für ein selbstsigniertes Zertifikat.
- **Das Ziel ist ein Dienst auf dieser Box selbst**: nötig für ein Ziel wie `localhost`, den Namen der Box oder eine ihrer Adressen ([`proxy.target.on_box`](/de/docs/reference/errors/#proxy.target.on_box)), zum Beispiel um die Weboberfläche auf 7484 unter einer Domain zu erreichen. Die Ports 80 und 443 der Box werden immer abgelehnt, weil die Anfrage wieder beim Proxy ankäme ([`proxy.target.loop`](/de/docs/reference/errors/#proxy.target.loop)).
- **WebSockets weiterleiten**: standardmäßig an; ausgeschaltet bekommen Upgrades 403 ([`proxy.websockets.disabled`](/de/docs/reference/errors/#proxy.websockets.disabled)).

Antworten fließen ungepuffert durch, Server-Sent Events und große Downloads kommen also beim Browser an, sobald sie entstehen. Mit Zielen spricht der Proxy HTTP/1.1.

### HTTPS

- **Zertifikat**: **Von der Zertifizierungsstelle der Box** stellt im Speicher ein Zertifikat für die Domains des Hosts aus (397 Tage, erneuert 30 Tage vor dem Ende und immer, wenn sich die Zertifizierungsstelle ändert). Jede Domain muss im Bereich der Zertifizierungsstelle liegen ([`tls.name.not_permitted`](/de/docs/reference/errors/#tls.name.not_permitted)); deine eigene Domain nimmst du mit einer neuen Zertifizierungsstelle in Einstellungen > **Zertifikate** in den Bereich auf. **Eigenes hochladen** nimmt ein Zertifikat mit Kette und unverschlüsseltem Schlüssel im PEM-Format, das jede Domain abdeckt ([`proxy.certificate.name_mismatch`](/de/docs/reference/errors/#proxy.certificate.name_mismatch)), zum Beispiel eines für eine öffentliche Domain. Ein abgelaufener oder kaputter Upload wird durch das eigene Zertifikat der Box ersetzt, und der Host zeigt **Kein Zertifikat**, wenn er keines hat.
- **Von Let's Encrypt** holt ein kostenloses Zertifikat, dem jedes Gerät vertraut, nur für öffentliche Domains; siehe [Let's Encrypt](#lets-encrypt).
- Die Box wählt das Zertifikat nach dem Namen, den der Browser beim TLS-Handshake nennt, auf 443 und auf 7484; ein Name ohne Host bekommt das Zertifikat der Box.
- **HTTP auf HTTPS umleiten** (standardmäßig an) beantwortet einfaches HTTP auf 80 mit einer Umleitung auf 443 (301 für GET und HEAD, sonst 308). Ausgeschaltet antwortet der Host auch auf 80.
- **HTTP/2 anbieten** (standardmäßig an) lässt Browser auf 443 HTTP/2 nutzen.
- **HSTS senden** setzt `Strict-Transport-Security: max-age=31536000` auf HTTPS-Antworten.

:::caution[HSTS]
Mit HSTS lehnt ein Browser für den Namen ein Jahr lang einfaches HTTP und jede Zertifikatswarnung ab, auch wenn du es wieder ausschaltest. Nutze es nur mit einem Zertifikat, dem jedes Gerät vertraut.
:::

### Zugriff

Der Proxy prüft in dieser Reihenfolge: die erlaubten und gesperrten Adressen, Basic Auth, dann die Pithyx-Anmeldung.

- **Erlaubte Adressen** und **Gesperrte Adressen**: IP-Adressen oder Bereiche wie `192.168.1.0/24`. Eine gesperrte Adresse kommt nie herein; mit erlaubten Adressen nur diese ([`proxy.address.denied`](/de/docs/reference/errors/#proxy.address.denied)). Die Box sieht die Adresse, von der die Verbindung kommt.
- **Basic Auth**: Benutzer mit einem Passwort, nach dem der Browser vor allem anderen fragt. Passwörter werden als Argon2id-Hashes gespeichert und nie angezeigt; lass das Feld leer, um ein gespeichertes Passwort zu behalten. Fehlversuche zählen zu denselben Grenzen wie fehlgeschlagene Anmeldungen. Das Ziel bekommt den Header `Authorization` nicht.
- **Pithyx-Anmeldung verlangen**: Eine Seite des Hosts schickt Besucher ohne Zugang zur Anmeldung der Box, standardmäßig auf dem aufgerufenen Namen und Port 7484 (oder an die **Anmeldeadresse** der Seite). Nach der Anmeldung dort kommt der Browser mit einem Einmal-Ticket (30 Sekunden) zu `/.pithyx/auth` auf dem Host zurück, der ein Cookie des Hosts setzt und zur Seite weitergeht. Anfragen, die keine Seite laden, bekommen 401 ([`proxy.auth.required`](/de/docs/reference/errors/#proxy.auth.required)). Das Cookie gehört zur Sitzung auf der Box: Abmelden von der Box, eine widerrufene Sitzung, das verlorene Recht zur Desktop-Anmeldung oder das Entfernen aus den Benutzern und Gruppen des Hosts beendet den Zugang bei der nächsten Anfrage. **Benutzer** und **Gruppen** begrenzen, wer hereinkommt ([`proxy.access.denied`](/de/docs/reference/errors/#proxy.access.denied)); ohne beide kommt jeder herein, der sich anmelden darf, Administratoren immer. Das Ziel bekommt den Benutzer in `X-Pithyx-User`, `X-Pithyx-User-Id`, `X-Pithyx-Groups` und `X-Pithyx-Admin`. Der Pfad `/.pithyx/auth` eines solchen Hosts gehört dem Proxy. Benutzer und Gruppen werden mit Namen gespeichert: Nach dem Umbenennen einer Gruppe passe den Host an.

:::note
Die Anmeldung läuft über die Weboberfläche der Box. Erreichst du einen Host von außen nur über 443, setze die **Anmeldeadresse** auf einen Host, der zur Weboberfläche der Box führt, etwa einen Proxy-Host für `nas.example.org` mit dem Ziel `https://localhost:7484`.
:::

### Header

- Das Ziel bekommt nie eingehende `X-Pithyx-*`-, `X-Forwarded-*`-, `Forwarded`- oder Trace-Header und auch nicht die eigenen Cookies der Box (`pithyx_*`); eine Antwort kann diese Cookies auch nicht setzen.
- **X-Forwarded-For, -Proto und -Host senden** (standardmäßig an) nennt dem Ziel die Adresse des Clients, das Protokoll und den aufgerufenen Namen.
- **Den aufgerufenen Namen als Host senden** gibt den `Host` des Browsers weiter; sonst bekommt das Ziel seinen eigenen Host und Port. **Host-Header** setzt stattdessen einen eigenen `Host`.
- **Anfrage-Header** und **Antwort-Header**: einer pro Zeile als `Name: Wert`; `Name:` allein entfernt den Header. Header, die der Proxy selbst steuert (`Host`, `Connection`, `X-Forwarded-*`, `X-Pithyx-*`, `Strict-Transport-Security` und andere), werden abgelehnt ([`proxy.header.invalid`](/de/docs/reference/errors/#proxy.header.invalid)).

### Grenzen und Protokoll

- **Größter Upload in MB**: leer für keine Grenze.
- **Verbindungs-Timeout** (1 bis 300 Sekunden, Standard 10) und **Antwort-Timeout** (1 bis 86400 Sekunden, Standard 100: wie lange das Ziel schweigen darf). Ein ruhender WebSocket bleibt bis zu einem Tag offen. Ein unerreichbares Ziel ergibt 502 ([`proxy.target.unavailable`](/de/docs/reference/errors/#proxy.target.unavailable)), ein zu langsames 504 ([`proxy.target.timeout`](/de/docs/reference/errors/#proxy.target.timeout)).
- **Zugriffslog schreiben** (standardmäßig an): jede Anfrage im Log Center unter der Quelle `proxy/<erste Domain>`, mit Methode, Name, Pfad (ohne die Abfrage, die Geheimnisse enthalten kann), Status, Dauer, Adresse des Clients und, hinter einer Anmeldung, dem Benutzer.

Die Liste zeigt für jeden Host, ob sein Ziel beim Öffnen der Seite geantwortet hat; **Ziel prüfen** schaut noch einmal nach.

### Let's Encrypt

Ein Proxy-Host oder Weiterleitungs-Host mit dem Zertifikat **Von Let's Encrypt** bekommt ein Zertifikat für alle seine Domains von Let's Encrypt (oder einer anderen ACME-Zertifizierungsstelle) und erneuert es selbst ein Drittel seiner Laufzeit vor dem Ende, bei den üblichen 90 Tagen also 30 Tage vorher. Die Box spricht nur für diese Hosts mit der Zertifizierungsstelle.

1. Lies im Abschnitt **Let's Encrypt** unter Einstellungen > **Reverse Proxy** die **Nutzungsbedingungen**, stimm ihnen zu und speichere. Eine E-Mail-Adresse ist optional; die Zertifizierungsstelle darf dir dorthin zu dem Konto schreiben. Eine andere Zertifizierungsstelle kommt in **ACME-Verzeichnis** (eine `https`-Adresse) und braucht eine eigene Zustimmung.
2. Lass im DNS jede Domain des Hosts auf die öffentliche Adresse der Box zeigen und leite Port 80 (und 443 für die Besucher) deines Routers an die Box weiter. Die Zertifizierungsstelle prüft jeden Namen über Port 80 (HTTP-01); diese Prüfung beantwortet die Box selbst, für jeden Host.
3. Leg den Host mit dem Zertifikat **Von Let's Encrypt** an oder bearbeite ihn. Die erste Anfrage startet sofort.

Der Host zeigt, wo sein Zertifikat steht: wartet auf das erste, gültig bis zu einem Datum, oder warum die letzte Anfrage fehlschlug, mit den Worten der Zertifizierungsstelle, etwa [`proxy.acme.dns`](/de/docs/reference/errors/#proxy.acme.dns) (ein Name hat keine Adresse), [`proxy.acme.connection`](/de/docs/reference/errors/#proxy.acme.connection) (Port 80 erreicht die Box nicht aus dem Internet), [`proxy.acme.unauthorized`](/de/docs/reference/errors/#proxy.acme.unauthorized), [`proxy.acme.caa`](/de/docs/reference/errors/#proxy.acme.caa) oder [`proxy.acme.rate_limited`](/de/docs/reference/errors/#proxy.acme.rate_limited). Nach einem Fehler versucht es die Box nach einer Stunde wieder und wartet dann jedes Mal doppelt so lange, bis zu einem Tag; **Jetzt erneuern** fragt sofort. Bis das erste Zertifikat da ist, oder wenn eines nicht erneuert werden konnte, zeigt der Host das eigene Zertifikat der Box. Eine fehlgeschlagene Erneuerung benachrichtigt die Administratoren.

Wildcards ([`proxy.acme.wildcard`](/de/docs/reference/errors/#proxy.acme.wildcard)) und lokale Namen wie `nas.home.arpa`, `.local`, Namen ohne Punkt oder IP-Adressen ([`proxy.acme.name_not_public`](/de/docs/reference/errors/#proxy.acme.name_not_public)) bekommen kein Zertifikat von einer öffentlichen Zertifizierungsstelle; nimm dafür die eigene der Box. Ohne Zustimmung zu den Bedingungen lehnt ein Host die Wahl mit [`proxy.acme.terms_required`](/de/docs/reference/errors/#proxy.acme.terms_required) ab, ohne den einfachen Proxy-Port 80 mit [`proxy.acme.port_unavailable`](/de/docs/reference/errors/#proxy.acme.port_unavailable).

## Weiterleitungs-Hosts

Ein Weiterleitungs-Host schickt Browser von seinen Domains mit 301, 302, 307 oder 308 zu einer anderen Adresse. Mit **Pfad und Abfrage behalten** geht eine Anfrage für `/a?b=1` an die Zieladresse, gefolgt von `/a?b=1`. Auf 443 zeigt er ein Zertifikat vor wie ein Proxy-Host.

## Namen ohne Host

**Namen ohne Host** legt fest, was 443 und 80 mit einem Namen tun, den kein aktiver Host hat: **Auf die Weboberfläche auf Port 7484 umleiten** (Standard, wie ohne Reverse Proxy) oder **Mit „nicht gefunden“ (404) antworten** ([`proxy.host.unknown`](/de/docs/reference/errors/#proxy.host.unknown)). Die Prüfung `/api/install-id` des Installers auf Port 80 antwortet immer.

## Streams

Ein Stream leitet einen Port der Box, TCP, UDP oder beides, Byte für Byte an einen Host und Port weiter, für Dienste, die kein HTTP sprechen. Die Firewall öffnet den Port, solange der Stream aktiv ist. Die eigenen Ports der Box werden abgelehnt (21, 22, 80, 443, 445, 7484 und die passiven FTP-Ports 50000 bis 50099, [`proxy.stream.port_reserved`](/de/docs/reference/errors/#proxy.stream.port_reserved)), ebenso Ports, die ein anderes Programm belegt ([`proxy.stream.port_in_use`](/de/docs/reference/errors/#proxy.stream.port_in_use)), und Ports unter 1024, außer du bestätigst sie. Ein UDP-Client behält seine eigene Verbindung zum Ziel, bis er eine Minute lang schweigt; ein Stream nimmt bis zu 512 TCP-Verbindungen und 1024 UDP-Clients gleichzeitig an.

## Sicherheit

Der Proxy läuft im Daemon, der als root läuft, und nur Administratoren dürfen Ziele festlegen. Anfragen, die der Proxy selbst geschickt hat, erkennt er und lehnt sie mit 508 ab ([`proxy.loop.detected`](/de/docs/reference/errors/#proxy.loop.detected)). Die Einstellungen sind im Konfigurations-Backup als Teil **Reverse Proxy**; hochgeladene Zertifikate und die von Let's Encrypt kommen mit dem Teil **Zertifikate** (Dateien `proxy-host-<id>.pem`, `proxy-host-<id>-acme.pem` (`proxy-redirect-<id>-acme.pem`) und ihre `.key` neben denen der Box, mit dem Kontoschlüssel `acme-account-<hash>.key`).

## Noch nicht gebaut

Lastverteilung mit Health-Checks, Regeln pro Pfad, Ratenbegrenzung und Zertifikate über DNS-01 (nötig für Wildcards) kommen später.
