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

Die Hooks führen vor einem Commit Prettier, ESLint, die Repository-Prüfungen, shellcheck und `dotnet format whitespace` aus, beim Commit zusätzlich eine Prüfung der Commit-Nachricht (Gitmoji-Format). Ein Pre-Push-Hook lehnt Pushes auf `master` und `dev` ab. Warnungen sind in C# und TypeScript Fehler.

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

Die Simulation liest gefälschte Kontodateien aus den Fixtures und wendet nichts auf das System an. Hat ein Befehl keine Fixture, sagt der Daemon "No simulation fixture for: ..."; nimm eine mit `tools/fixtures/record.sh` auf.

Wenn deine Entwicklungsmaschine selbst Pithyx betreibt (`/etc/pithyx/environment` existiert) und du einen Daemon ohne `PITHYX_DB` startest, beendet er sich mit Code 78. Setze `PITHYX_DB`.

## Tests

- **Daemon:** `dotnet test --solution daemon/Pithyx.slnx` (xUnit). Die Ordner unter `daemon/tests/Pithyx.Daemon.Tests` spiegeln die Module. `DaemonFactory` startet den Daemon im Speicher mit einem temporären Datenordner, eingeschalteter Simulation und einem `FakeTimeProvider`; `CreateApiClient()` fügt den CSRF-Header hinzu.
- **Frontend:** `pnpm lint`, `pnpm typecheck`, `pnpm test` (Vitest).
- **Ende zu Ende:** `pnpm --filter @pithyx/e2e exec playwright install chromium`, dann `pnpm build && pnpm e2e`. Das startet einen eigenen Daemon in der Simulation mit einer frischen Datenbank.
- **Layout-Prüfungen:** `pnpm design:test` prüft jeden Bildschirm auf Überlauf in den Größen Smartphone, Tablet und Laptop in Englisch und Deutsch.
- **Gemeinsame Korpora:** Die Fixtures für Manifest, Compose und Katalog in `sdk/schema/fixtures/` laufen in Vitest und xUnit, sodass das CLI und der Daemon gleich urteilen.

Teste Parser, Entscheidungslogik, Berechtigungsprüfungen, Manifest-Validierung und Zustandsautomaten. Teste keine Getter, DTOs, durchreichenden Controller oder einzelne React-Komponenten.

Die Kategorien `StorageIntegration`, `DockerIntegration`, `NetworkIntegration` und `TimingBudget` laufen im normalen Daemon-Testlauf der CI nicht.

## Integrationstests

Diese Suiten brauchen root, echtes ZFS, Loop-Devices, `scsi_debug`, Docker oder systemd-networkd und laufen deshalb in einer virtuellen Debian-13-Maschine, nicht auf deiner Workstation. Die in der README beschriebene VM hat 4 vCPUs, 8 GB RAM, UEFI, eine Systemfestplatte mit 32 GB und sechs Datenplatten mit je 10 GB und den Seriennummern `VYXTEST01` bis `VYXTEST06`.

- `sudo tools/dev-vm/provision.sh` bereitet die Maschine vor (gefahrlos wiederholbar; Exit-Code 2 heißt: neu starten und noch einmal ausführen). Es schreibt `dev` nach `/etc/pithyx/environment` und die Test-Seriennummern nach `/etc/pithyx/test-disks`.
- `tools/dev-vm/testpool.sh create|destroy|wipe` verwaltet Test-Pools und lehnt Festplatten ab, die keine Testplatten sind. Führe nach `zpool destroy` `testpool.sh wipe` aus.
- `tools/dev-vm/integration.sh [--filter <class>] [--disk-pulls] [--docker] [--network]` führt die Suiten als normaler Benutzer aus und baut zuvor in Release. `--disk-pulls` zieht virtuelle Festplatten aus einem laufenden Pool; nutze es nur auf einer frisch gestarteten Maschine. `--docker` und `--network` schließen sich aus.

Last verfälscht Zeitbudgets: Prüfe `uptime`, bevor du einem Fehlschlag traust.
