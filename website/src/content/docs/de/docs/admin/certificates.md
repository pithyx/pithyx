---
title: 'Zertifikate'
description: 'HTTPS auf 7484, die Box als eigene Zertifizierungsstelle, ihr auf deinen Geräten vertrauen, Zertifikate für andere Geräte und ein eigenes Zertifikat.'
status: ready
audience: admin
since: '1.0.0'
sidebar:
  order: 10
---

Die Box spricht HTTPS auf Port 7484 mit einem Zertifikat ihrer eigenen Zertifizierungsstelle; die Ports 80 und 443 leiten dorthin um, außer für die Domains des [Reverse Proxys](/de/docs/admin/reverse-proxy/), die eigene Zertifikate vorzeigen. Installiere das Root-Zertifikat der Zertifizierungsstelle einmal auf jedem Gerät, dann vertraut der Browser der Box, lädt aus seinem Cache, und FTPS-Programme akzeptieren sie. Alles auf dieser Seite findest du unter Einstellungen > **Zertifikate** (nur Administratoren).

## Warum der Browser warnt

Beim ersten Öffnen von `https://<box-adresse>:7484` warnt der Browser, dass die Verbindung nicht privat ist: Er kennt nicht, wer das Zertifikat der Box signiert hat. Das ist so gewollt. Fahre zur Seite fort, melde dich an und vertraue der Box wie unten beschrieben; danach ist die Warnung auf diesem Gerät weg. Bis dahin behalten Chromium-basierte Browser nichts von der Box in ihrem Cache: Jedes Neuladen lädt den Desktop neu, und jedes App-Fenster lädt seine App neu.

## Der Box auf einem Gerät vertrauen

Wähle unter **Dieser Box vertrauen** **Root-Zertifikat herunterladen** (die Datei `pithyx-root-ca.crt`; jeder darf sie herunterladen, auch vor der Anmeldung, unter `/api/system/tls/root-ca.crt`). Bevor du ihr vertraust, vergleiche ihren SHA-256-Fingerabdruck mit dem auf der Seite. Dann:

- **Windows**: Öffne die Datei, wähle **Zertifikat installieren**, **Lokaler Computer** und dann den Speicher **Vertrauenswürdige Stammzertifizierungsstellen**.
- **macOS**: Öffne die Datei in der Schlüsselbundverwaltung (Schlüsselbund **System**), öffne das Zertifikat und stelle **Vertrauen** auf **Immer vertrauen**.
- **iPhone und iPad**: Öffne die Datei in Safari, installiere das Profil unter Einstellungen, Allgemein, VPN und Geräteverwaltung und schalte es unter Einstellungen, Allgemein, Info, Zertifikatsvertrauenseinstellungen ein.
- **Android**: Einstellungen, Sicherheit, Verschlüsselung und Anmeldedaten, Zertifikat installieren, CA-Zertifikat, dann die Datei wählen.
- **Linux**: Kopiere die Datei nach `/usr/local/share/ca-certificates/` und führe `sudo update-ca-certificates` aus.
- **Firefox** hat einen eigenen Speicher: Einstellungen, Datenschutz und Sicherheit, Zertifikate, Zertifikate anzeigen, Zertifizierungsstellen, Importieren, und ihm für Websites vertrauen.

## Die Zertifizierungsstelle

Die Zertifizierungsstelle hat zwei Teile: ein Root-Zertifikat (ECDSA P-384, 20 Jahre), dem Geräte vertrauen, und eine Zwischenzertifizierungsstelle (ECDSA P-256, 5 Jahre), die jedes Zertifikat signiert. Die Zwischenstelle erneuert sich ein Jahr vor ihrem Ende selbst; Geräte merken davon nichts, weil sie dem Root-Zertifikat vertrauen.

Die erste Zertifizierungsstelle ist per Name Constraints auf lokale Namen und private Adressen begrenzt: die Endungen `local`, `lan`, `home.arpa`, `internal` und `localhost`, den Hostnamen der Box, die Suchdomains des Netzes und die Adressbereiche 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 100.64.0.0/10, 169.254.0.0/16, 127.0.0.0/8, fc00::/7, fe80::/10 und ::1. Ein Gerät, das dem Root-Zertifikat vertraut, akzeptiert nichts anderes von ihr; selbst wer die Box übernimmt, kann also kein Zertifikat für eine öffentliche Website fälschen. Root und Zwischenstelle tragen beide die Grenze.

**Neue Zertifizierungsstelle anlegen…** ersetzt Root und Zwischenstelle. Mit **Nur lokale Namen und private Adressen** (Standard) kannst du eigene Domains ergänzen, etwa `home.example.org`; jede gilt für alle Namen darunter. Ohne die Grenze darf die Zertifizierungsstelle jeden Namen signieren, und ein Gerät, das ihr vertraut, würde ein gefälschtes Zertifikat für jede Website akzeptieren, wenn die Box übernommen würde. Nach einer neuen Zertifizierungsstelle muss jedes Gerät dem neuen Root-Zertifikat erneut vertrauen; für andere Geräte ausgestellte Zertifikate bleiben nur dort gültig, wo das alte Root-Zertifikat noch vertraut wird, und die Liste markiert sie mit **Frühere Zertifizierungsstelle**. Auch eine Umbenennung der Box braucht eine neue Zertifizierungsstelle, wenn du sie über ihren bloßen Hostnamen erreichst, weil der alte Hostname Teil der Grenze ist.

## Das eigene Zertifikat der Box

Das Zertifikat der Box gilt für ihren Hostnamen, `<host>.local`, den Hostnamen in jeder Suchdomain, `localhost`, jede Adresse ihrer Netzwerkkarten (nicht die der App-Container) und die Loopback-Adressen, soweit die Zertifizierungsstelle sie signieren darf. Es läuft 397 Tage, erneuert sich 30 Tage vor seinem Ende und wird sofort neu ausgestellt, wenn sich eine Adresse oder der Hostname ändert. **Namen des Box-Zertifikats** zeigt, wofür es gilt; **Weitere Namen** ergänzt Namen wie eine Domain, die auf die Box zeigt. Sie müssen innerhalb der Grenze der Zertifizierungsstelle liegen (sonst [`tls.name.not_permitted`](/de/docs/reference/errors/#tls.name.not_permitted)).

## Zertifikate für andere Geräte

**Zertifikat ausstellen…** erstellt ein Serverzertifikat für einen Drucker, Router oder einen anderen Server in deinem Netz: Namen und Adressen (ein führendes `*.` für eine Wildcard), 1 bis 825 Tage und ein ECDSA-Schlüssel oder, für Geräte, die ihn brauchen, RSA mit 2048 Bit. Der Browser lädt das Zertifikat mit der Zwischenstelle (`.crt`) und den privaten Schlüssel (`.key`) herunter. Die Box behält den Schlüssel nicht. In der Liste kannst du das Zertifikat ohne Schlüssel erneut herunterladen und einen Eintrag entfernen; die Box führt keine Sperrliste, ein entferntes Zertifikat bleibt also gültig, bis es endet.

## Ein eigenes Zertifikat

**Eigenes Zertifikat** nimmt ein Zertifikat, das du anderswo bekommen hast, etwa für deine eigene Domain: das Zertifikat mit seinen Zwischenzertifikaten und den privaten Schlüssel, beide in PEM und den Schlüssel ohne Passwort. Die Box zeigt es auf 7484, 443 und für FTPS, solange es gültig ist, und ihr eigenes wieder, sobald es abgelaufen ist oder nach **Eigenes Zertifikat entfernen**. Der Upload wird mit [`tls.custom.invalid`](/de/docs/reference/errors/#tls.custom.invalid), [`tls.custom.key_mismatch`](/de/docs/reference/errors/#tls.custom.key_mismatch) oder [`tls.custom.expired`](/de/docs/reference/errors/#tls.custom.expired) abgelehnt. Let's Encrypt ist noch nicht eingebaut.

## Hinter einem Reverse Proxy

Um andere Dienste hinter die Box zu stellen, nimm ihren eigenen [Reverse Proxy](/de/docs/admin/reverse-proxy/). Ein Reverse Proxy anderswo vor der Box verbindet sich mit `https://<box-adresse>:7484`. Lass ihn dem Root-Zertifikat der Box vertrauen (oder, weniger streng, die Prüfung für dieses eine Backend auslassen), WebSocket-Upgrades für `/ws` und App-Sockets durchreichen und seine Anfragen mit dem Hostnamen senden, den der Browser benutzt hat. Die Box sieht die Adresse des Proxys als Client, daher gilt die Grenze für fehlgeschlagene Anmeldungen pro Adresse für alle hinter dem Proxy gemeinsam.

## Sicherung

Root, Zwischenstelle, das Zertifikat der Box, ein hochgeladenes Zertifikat, die weiteren Namen und die Liste der ausgestellten Zertifikate gehören zur Konfigurationssicherung (Teil **Zertifikate**). Stellst du sie auf einem neuen System wieder her, kommt dieselbe Zertifizierungsstelle zurück, und Geräte vertrauen ihr weiter; die Box stellt ihr eigenes Zertifikat für die Namen des neuen Systems neu aus.
