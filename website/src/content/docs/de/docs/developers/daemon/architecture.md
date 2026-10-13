---
title: 'Architektur des Daemons'
description: 'Der einzige privilegierte Prozess: seine Module, der Command Runner, Storage-Backends und Pfade.'
status: ready
audience: developer
sidebar:
  order: 1
---

Der Daemon (`pithyxd`) ist der einzige privilegierte Prozess auf einer Pithyx-Box: eine C#-Binärdatei (.NET 10, ASP.NET Core) mit einer SQLite-Datenbank. Er liefert den Desktop und die API aus, führt jeden Systembefehl über einen einzigen Command Runner aus und verwaltet Speicher, Konten, Dateidienste, Apps und Docker. Der Desktop läuft nie mit erhöhten Rechten.

## Überblick

Der Daemon ist ein modularer Monolith. Jedes Modul ist ein Ordner unter `daemon/src/Pithyx.Daemon`: Apps, Auth, Backup, Commands, Database, Errors, FileServices, Files, Health, Hosting, Installer, Jobs, Layouts, Localization, Logs, Monitoring, Network, Notifications, Permissions, Realtime, Shares, Storage, SystemSettings, Updates und Users. Die Solution ist `daemon/Pithyx.slnx`, die Tests liegen in `daemon/tests/Pithyx.Daemon.Tests`.

- **Prozess.** `pithyx.service` startet `/usr/lib/pithyx/daemon/pithyxd`. Die Unit startet ihn bei einem Fehler nach 2 Sekunden neu, erlaubt 5 Starts in 120 Sekunden und startet bei den Exit-Codes 65 und 78 nicht neu.
- **Ports.** Kestrel lauscht auf 7484. Im echten Modus spricht 7484 TLS (`Hosting/WebTransport`): Eine Verbindung, deren erstes Byte kein TLS-Handshake ist, geht an reines HTTP und wird auf `https://` umgeleitet. Auf einer echten Box (Produktions-Build) lauscht er zusätzlich auf 443 (TLS) und 80 (ein Entwicklungs-Build auf den Ports aus `PITHYX_DEV_PROXY_PORTS`). Dort läuft zuerst `ReverseProxy/HostProxy`: Es leitet die Domains der Proxy-Hosts mit YARPs `IHttpForwarder` weiter, leitet die der Weiterleitungs-Hosts um und überlässt jeden anderen Namen `TransportRedirect`, das auf 7484 umleitet, außer `/api/install-id` auf 80 (D347). Jeder TLS-Handshake bekommt sein Zertifikat nach Servernamen aus `ReverseProxy/ProxyService`. Simulation und das Live-System des Installers liefern reines HTTP. Die Zertifikate kommen aus `Tls/BoxCertificates` (D346).
- **Anfragen.** Jeder `/api`-Endpunkt verlangt eine Sitzung, sofern er nicht als anonym markiert ist. Die Richtlinien heißen `admin`, `power` und `appToken`.
- **Statische Dateien.** Der Daemon liefert den Desktop aus `/usr/lib/pithyx/desktop` (oder `PITHYX_DESKTOP_DIR`) aus, mit einem Fallback auf die Single-Page-App.
- **Modi.** Der echte Modus spricht mit dem System. Der Simulationsmodus (`PITHYX_SIMULATE=1`) antwortet aus Fixtures; siehe [Am Daemon entwickeln](/de/docs/developers/daemon/development/).
- **Installer-Modus.** Mit `PITHYX_INSTALLER=1` oder der Kernel-Kommandozeilenoption `pithyx.installer` gibt es nur die Endpunkte des Installers und der Einrichtung.
- **Exit-Codes.** 1 bedeutet, dass die Datenbank fehlgeschlagen ist, 65, dass die Datenbank von einem neueren Daemon geschrieben wurde, 75 bittet systemd um einen Neustart (nach einer Wiederherstellung der Konfiguration) und 78 bedeutet eine zweite Installation (siehe unten).

Benutzer-IDs pro App kommen aus dem Bereich 40000 bis 49999, Benutzerkonten aus 20000 bis 29999 und Gruppen aus 30000 bis 39999. IDs werden nie wiederverwendet.

Apps laufen als Container: Der Daemon spricht mit Docker über dessen Unix-Socket und startet ein Backend als Job mit `docker compose up --wait`. Siehe [Backends](/de/docs/developers/apps/backend/).

Der Daemon hat keinen Watchdog (`sd_notify`-Heartbeats) und kein Modul für virtuelle Maschinen.

## Command Runner

Jeder externe Befehl läuft über `ICommandRunner`. Code ruft `zfs`, `zpool` oder `smartctl` nie direkt auf.

- `RunAsync(CommandRequest, ct)` liefert ein `CommandResult`. `StreamLinesAsync` ist für langlebige Befehle: Das Timeout der Anfrage gilt dort nicht, und ein Abbruch beendet den Prozess.
- Ein `CommandRequest` hat den Dateinamen, eine Argumentliste (keine Shell), ein optionales Timeout, Indizes sensibler Argumente und optional die Standardeingabe. Die Standardeingabe wird einmal geschrieben, geschlossen und nie geloggt.
- Das Standard-Timeout beträgt 1 Minute. Ein Timeout beendet den ganzen Prozessbaum.
- Der Runner setzt `LC_ALL=C.UTF-8` und übergibt die aktuelle Trace-ID als `TRACEPARENT`, sodass sich ein Befehl seiner Anfrage zuordnen lässt.
- Jeder Aufruf wird mit Befehl, Argumenten, Exit-Code, Dauer und der auf 4096 Zeichen gekürzten Ausgabe geloggt. Sensible Argumente erscheinen als `***`.
- Im Simulationsmodus antwortet `FixtureCommandRunner` aus einem Szenario-Ordner mit den Dateien `<id>.json`, `<id>.stdout`, optional `<id>.stderr` und `<id>.stream`. Neue Fixtures nimmst du mit `tools/fixtures/record.sh <scenario> <id> -- <command> [args...]` auf; das Skript anonymisiert Seriennummern und WWNs.

Parser für Befehlsausgaben sind reine statische Funktionen mit Fixture-Tests, zum Beispiel `LsblkParser`, `ZfsDatasetParser` und `ZfsSnapshotParser`.

## Storage-Backend

`IStorageBackend` hat zwei Member: `ReadAsync` liefert die Pool-Liste und `WatchAsync` liefert einen Wert pro Änderung. `ZfsBackend` ist die einzige Implementierung; es gibt kein Btrfs- oder mdadm-Backend.

Darum herum liegen die Inventare für Festplatten und Pools, die Planer (`StoragePlanner`, `DatasetPlanner`, `PoolImportScanner`) und die Befehlspläne (`ZfsPlans`, `SnapshotPlans`, `ZfsScrubPlans`, `ImportPlans`), die dem Benutzer gezeigt werden, bevor sie laufen. `DiskGuard` lehnt zerstörerische Aktionen auf Festplatten mit Daten ab, solange der Benutzer nicht mit der Seriennummer der Festplatte bestätigt.

Der Speicherzustand erreicht die Clients über die Topics `storage/disks`, `storage/pools` und `storage/alerts`.

## Datenbank und Migrationen

Der Daemon nutzt SQLite über Dapper mit explizitem SQL, ohne ORM und ohne Repository-Schicht. Die Datenbank ist `/var/lib/pithyx/pithyx.db` oder der Pfad in `PITHYX_DB`. Sie läuft im WAL-Modus. Logs liegen in einer eigenen `logs.db`.

- Migrationen sind eingebettete Dateien `daemon/src/Pithyx.Daemon/Database/Migrations/NNNN_name.sql` (heute 28). Jede läuft in einer eigenen Transaktion; ein Fehler beendet mit Code 1. Die Tabelle `schema_version` hält fest, was gelaufen ist.
- Bevor eine nicht leere Datenbank migriert wird, schreibt der Daemon mit `VACUUM INTO` eine Kopie namens `pithyx.db.pre-<NNNN>-<time>.bak`, wobei `NNNN` die erste ausstehende Version ist.
- Enthält die Datenbank Versionen, die der Daemon nicht kennt, loggt er eine kritische Meldung und beendet sich mit 65. Starte einen neueren Daemon oder richte `PITHYX_DB` auf eine andere Datenbank.
- Daten werden als UTC-Text nach ISO 8601 gespeichert.
- Die Konfigurationssicherung umfasst die Datenbank, die Samba-Passwortdatenbank, TLS-Zertifikate und SSH-Host-Schlüssel. Eine neue Tabelle muss zu einem Sicherungsteil gehören oder als Laufzeitzustand markiert werden.

## Pfade auf der Box

| Pfad                          | Inhalt                                                                 |
| ----------------------------- | ---------------------------------------------------------------------- |
| `/var/lib/pithyx/pithyx.db`   | Die Datenbank (überschreibbar mit `PITHYX_DB`)                         |
| `/var/lib/pithyx/catalogs/`   | Ein Klon pro aktiviertem Katalog                                       |
| `/var/lib/pithyx/tls/`        | Die Zertifizierungsstelle der Box, ihr eigenes und ein hochgeladenes Zertifikat (PEM, nur für root lesbar)                 |
| `/var/log/pithyx/`            | `daemon.json` und `logs.db` (überschreibbar mit `PITHYX_LOG_DIR`)      |
| `/var/cache/pithyx/`          | Caches (überschreibbar mit `PITHYX_CACHE_DIR`)                         |
| `/usr/lib/pithyx/desktop/`    | Der gebaute Desktop                                                    |
| `/etc/pithyx/environment`     | `dev` kennzeichnet eine Entwicklungsmaschine; alles andere bedeutet `prod` |
| `/<pool>/.pithyx/`            | App-Daten und Docker-Daten auf dem App-Pool, nie auf der Systemfestplatte |

Eine Maschine hat genau eine Pithyx-Installation. Ein Daemon, der aus einem Checkout ohne `PITHYX_DB` auf einer Maschine gestartet wird, die bereits `/etc/pithyx/environment` hat, verweigert den Start und beendet sich mit 78.
