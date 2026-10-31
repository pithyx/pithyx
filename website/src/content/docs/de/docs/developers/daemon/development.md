---
title: 'Am Daemon entwickeln'
description: 'Bauen, in der Simulation starten, Tests und die Integrationssuiten, die root brauchen.'
status: ready
audience: developer
sidebar:
  order: 4
---

Du kannst den Daemon auf jeder Linux-Maschine ohne ZFS oder root bauen und starten: Der Simulationsmodus beantwortet Systembefehle aus aufgezeichneten Fixtures, und die Unit-Tests laufen gegen einen In-Memory-Daemon mit einer Fake-Uhr. Nur die Integrationssuiten für Speicher, Docker und Netzwerk brauchen eine eigene virtuelle Maschine.

## Bauen

Voraussetzungen: .NET SDK 10.0.401 oder neuer (`global.json`), Node.js 24, pnpm (aktiviere es mit `corepack enable`) und git.

```sh
pnpm install            # also installs the git hooks (lefthook)
dotnet build daemon/Pithyx.slnx
pnpm build              # builds every package
pnpm generate           # builds the daemon, then regenerates the clients
```

Die Hooks führen vor einem Commit Prettier, ESLint, die Repository-Prüfungen, shellcheck und `dotnet format whitespace` aus, beim Commit zusätzlich eine Prüfung der Commit-Nachricht (Gitmoji-Format). Ein Pre-Push-Hook lehnt Pushes auf `stable` und `beta` ab, die sich nur über Pull Requests ändern. Warnungen sind in C# und TypeScript Fehler.

Der Dev-Server des Desktops läuft mit `pnpm dev` auf `http://localhost:5173` und leitet `/api` und `/ws` an einen Daemon auf `http://localhost:7484` weiter. `PITHYX_DAEMON_URL` ändert das Ziel.

## Simulationsmodus

```sh
PITHYX_SIMULATE=1 dotnet watch --project daemon/src/Pithyx.Daemon
```

Die Simulation funktioniert auf jedem Linux, auch unter WSL; natives Windows und macOS werden nicht unterstützt. Der Zustand landet in `.pithyx-dev/` (Datenbank, Logs, Cache), das git ignoriert; beende den Daemon und lösche den Ordner, um von vorn zu beginnen. Beim ersten Start fragt dich der Desktop nach dem Administrator und zeigt den Wiederherstellungsschlüssel einmal an.

| Variable                   | Bedeutung                                                             |
| -------------------------- | --------------------------------------------------------------------- |
| `PITHYX_SIMULATE`          | `1` schaltet die Simulation ein                                       |
| `PITHYX_FIXTURES`          | Fixture-Ordner (Standard `daemon/tests/fixtures`)                     |
| `PITHYX_SIMULATE_SCENARIO` | Szenario-Ordner (Standard `default`)                                  |
| `PITHYX_ETC_DIR`           | Ersatz für `/etc/pithyx` (Standard `<fixtures>/etc`)                  |
| `PITHYX_DB`                | Datenbankpfad                                                         |
| `PITHYX_LOG_DIR`           | Log-Ordner                                                            |
| `PITHYX_CACHE_DIR`         | Cache-Ordner                                                          |
| `PITHYX_DESKTOP_DIR`       | Gebauter Desktop, der ausgeliefert wird                               |
| `PITHYX_DEV_PORT`          | Port-Überschreibung, nur in Development-Builds                        |
| `PITHYX_FTP_PORT`          | FTP-Port (21 auf einer Box, 0 in der Simulation)                      |

Die Simulation liest gefälschte Kontodateien aus den Fixtures und wendet nichts auf das System an. Hat ein Befehl keine Fixture, sagt der Daemon "No simulation fixture for: ..."; nimm eine mit `tools/fixtures/record.sh` auf. Platten, die du nicht hast (SATA, NVMe, USB), liefert `tools/fixtures/record-lsblk-qemu.sh`: Es startet eine Wegwerf-VM in QEMU mit einer solchen Platte und nimmt dort `lsblk` auf; Modell und Hersteller sind dann die von QEMU.

Wenn deine Entwicklungsmaschine selbst Pithyx betreibt (`/etc/pithyx/environment` existiert) und du einen Daemon ohne `PITHYX_DB` startest, beendet er sich mit Code 78. Setze `PITHYX_DB`.

Während ein Entwicklungs-Build läuft, liegt die interaktive API-Referenz unter `http://localhost:7484/api/docs` und das OpenAPI-Dokument unter `/api/openapi.json`. Die Logs sind JSON-Zeilen in `.pithyx-dev/logs/daemon.json`; jeder Eintrag trägt die `RequestId`, die Fehlerantworten zurückgeben, also findet `grep <request id> .pithyx-dev/logs/daemon.json` alles, was bei einer Anfrage passiert ist.

## Design-Galerie

`pnpm design` startet einen Vite-Server auf Port 5174 (alle Schnittstellen) mit jedem Bildschirm des Desktops unter `/design.html`: Handy-, Tablet- und Laptopbreite, hell und dunkel, mit Schaltern für Akzentfarbe, Position der Taskleiste und Sprache. Sie ist ein Entwicklungswerkzeug; der Produktions-Build enthält nur `index.html`. `pnpm design:shots` rendert jeden Bildschirm als `<screen>_<viewport>_<theme>_<lang>.png` nach `tools/design-shots/out/` (`--screens login,tokens` begrenzt den Lauf, `--base-url` nutzt einen laufenden Server). Die Kern-Apps laufen dort echt mit erfundenen Beispieldaten (`desktop/src/design/storageSample.ts` und `appSamples.ts`), und `pnpm design:docs` rendert daraus die Screenshots der Website (`tools/design-shots/docs.ts`, D405).

## Tests

- **Daemon:** `dotnet test --solution daemon/Pithyx.slnx` (xUnit). Die Ordner unter `daemon/tests/Pithyx.Daemon.Tests` spiegeln die Module. `DaemonFactory` startet den Daemon im Speicher mit einem temporären Datenordner, eingeschalteter Simulation und einem `FakeTimeProvider`; `CreateApiClient()` fügt den CSRF-Header hinzu.
- **Frontend:** `pnpm lint`, `pnpm typecheck`, `pnpm test` (Vitest).
- **Ende zu Ende:** `pnpm --filter @pithyx/e2e exec playwright install chromium`, dann `pnpm build && pnpm e2e`. Das startet einen eigenen Daemon in der Simulation mit einer frischen Datenbank.
- **Design-Prüfungen:** `pnpm design:test` prüft jeden Bildschirm auf Überlauf in den Größen Smartphone, Tablet, Laptop und Ultrawide in Englisch und Deutsch und mit axe auf Barrierefreiheit und Kontrast in Hell und Dunkel. `DESIGN_PORT` legt seinen Server auf einen anderen Port als 5176.
- **Gemeinsame Korpora:** Die Fixtures für Manifest, Compose und Katalog in `sdk/schema/fixtures/` laufen in Vitest und xUnit, sodass das CLI und der Daemon gleich urteilen.

Teste Parser, Entscheidungslogik, Berechtigungsprüfungen, Manifest-Validierung und Zustandsautomaten. Teste keine Getter, DTOs, durchreichenden Controller oder einzelne React-Komponenten.

Die Kategorien `StorageIntegration`, `DockerIntegration`, `NetworkIntegration` und `TimingBudget` laufen im normalen Daemon-Testlauf der CI nicht.

## Integrationstests

Diese Suiten brauchen root, echtes ZFS, Loop-Devices, `scsi_debug`, Docker oder systemd-networkd und laufen deshalb in einer virtuellen Debian-13-Maschine, nicht auf deiner Workstation. Gib der VM 4 vCPUs, 8 GB RAM, UEFI, eine Systemfestplatte mit 32 GB und sechs Datenplatten mit je 10 GB und den Seriennummern `VYXTEST01` bis `VYXTEST06` (in Proxmox: `serial=VYXTEST01` an jeder Platte). Für Tests werden nur Platten mit diesen Seriennummern benutzt; mach vor zerstörerischen Läufen einen Snapshot der VM.

- `sudo tools/dev-vm/provision.sh` bereitet die Maschine vor (gefahrlos wiederholbar; Exit-Code 2 heißt: neu starten und noch einmal ausführen). Es schreibt `dev` nach `/etc/pithyx/environment` und die Test-Seriennummern nach `/etc/pithyx/test-disks`.
- `tools/dev-vm/testpool.sh create|destroy|wipe` verwaltet Test-Pools und lehnt Festplatten ab, die keine Testplatten sind. Führe nach `zpool destroy` `testpool.sh wipe` aus.
- `tools/dev-vm/integration.sh [--filter <class>] [--disk-pulls] [--docker] [--network]` führt die Suiten als normaler Benutzer aus und baut zuvor in Release. `--disk-pulls` zieht virtuelle Festplatten aus einem laufenden Pool; nutze es nur auf einer frisch gestarteten Maschine. `--docker` und `--network` schließen sich aus.

Last verfälscht Zeitbudgets: Prüfe `uptime`, bevor du einem Fehlschlag traust.

Eine Dev-VM hat genau eine Installation, wie eine Box: Die Unit `pithyx-dev.service` führt den Checkout als root im echten Modus auf Port 7484 aus, mit der Datenbank `/var/lib/pithyx/pithyx.db` auf der Systemfestplatte und ihren Pools auf den Testplatten. `tools/dev-vm/install-dev-daemon.sh` spult den Checkout auf `origin/dev` vor, baut, prüft, dass der Build jede Schemaversion der Datenbank kennt, und startet die Unit neu (`--yes` überspringt die Frage, `--no-build` nutzt den vorhandenen Build, `--uninstall` entfernt die Unit). `tools/dev-vm/reset-dev-db.sh` fängt mit einer leeren Datenbank neu an.

## Häufige Probleme

- **Port 7484 ist schon belegt:** Ein anderer Daemon läuft noch. Finde ihn mit `ss -ltnp | grep 7484`.
- **`pnpm: command not found`:** Führe `corepack enable` aus (mit `sudo`, wenn Node systemweit installiert wurde).
- **„Simulation fixtures not found“:** Der Daemon wurde aus einem ungewöhnlichen Ordner gestartet. Setze `PITHYX_FIXTURES` auf `daemon/tests/fixtures`.
- **Der Desktop zeigt „Daemon nicht erreichbar“:** Der Daemon läuft nicht, oder `PITHYX_DAEMON_URL` zeigt an die falsche Stelle.
- **`modprobe zfs` schlägt nach dem Provisioning fehl:** Starte neu, damit das DKMS-Modul zum laufenden Kernel passt, und führe `provision.sh` noch einmal aus. Secure Boot muss aus sein, weil das ZFS-Modul nicht signiert ist.
