---
title: 'Veröffentlichen'
description: 'Das Release-Werkzeug tools/release/pithyx-release: ISO und Update-Dateien bauen, signieren und die npm-Pakete veröffentlichen.'
status: ready
audience: developer
sidebar:
  order: 6
---

`tools/release/pithyx-release` ist ein Befehl über den Release-Skripten in `tools/release/`. Er kennt ihre Reihenfolge, Standardwerte und Voraussetzungen: Jeder Befehl wird zuerst zu einem Plan aus einfachen Befehlen, den `--dry-run` ausgibt und der sonst Schritt für Schritt läuft. Ohne Befehl zeigt er im Terminal den Stand des Checkouts, fragt, welcher Befehl laufen soll und nach dessen Optionen, gibt den Plan aus und führt ihn erst nach deiner Bestätigung aus.

```sh
tools/release/pithyx-release status
tools/release/pithyx-release update --version 1.0.0-beta.2 --sign --dry-run
tools/release/pithyx-release            # das Menü
```

## Befehle

| Befehl         | Was er tut                                                                                                                           |
| -------------- | ------------------------------------------------------------------------------------------------------------------------------------ |
| `status`       | Produkt- und npm-Version, der Checkout, die gebauten Pakete in `image/out`, ISOs, Update-Dateien, Baselines und der Beta-Schlüssel   |
| `checklist`    | Die Release-Schritte einer Version (`--version`, Standard ist die Produktversion) und welche davon dieser Rechner als erledigt zeigt |
| `iso`          | `image/build.sh` für eine Version: das ISO und seine Paketliste                                                                      |
| `update`       | `build-update.sh` gegen eine Baseline, mit `--sign` auch `sign-update.sh`                                                            |
| `sign`         | `sign-update.sh` für eine Datei, mit dem Beta-Schlüssel, solange `--key` und `--key-id` keinen anderen nennen                        |
| `channel`      | `build-channel.sh`: `channel.json` und seine Signatur für eine Datei (`--key`, `--key-id`, `--sequence`, `--url`)                    |
| `publish-beta` | `publish-beta.sh`: die Vorabversion `beta-<version>` und ein neues `channel-beta`                                                    |
| `publish-sdk`  | `publish-sdk.sh` für `@pithyx/sdk`, `@pithyx/ui` und `@pithyx/cli`; `--latest` setzt danach `latest` auf die Version                 |
| `sources`      | `collect-sources.ts --pack`: die GPL-Quellen eines Images als Release-Teile                                                          |
| `baseline`     | Kopiert die Paketliste eines gebauten Images nach `image/baseline/` (committe sie)                                                   |
| `templates`    | `bump-templates.ts` für lokale Klone der App-Vorlagen                                                                                |
| `key`          | `create-key.sh`: ein neuer Ed25519-Signaturschlüssel                                                                                 |

## Was es für dich prüft

- `update` baut aus den Paketen des letzten Laufs von `image/build.sh`. Sind sie von einer anderen Version, hält es an und sagt das; `--build-image` baut zuerst das Image der Version.
- Die Baseline eines Updates ist `--since <Datei>`, `--full` oder die Paketliste von `--minimum` (Standard `1.0.0`) aus `image/baseline/`, sonst aus `image/out/`. Fehlt sie, nennt es den Befehl, der sie aufbewahrt.
- Der Kanal ist beta für eine Vorabversion und sonst stable. Eine Beta-Datei wird mit `~/.config/pithyx-dev/pithyx-beta-1.pem` signiert; eine Stable-Datei braucht `--key` und `--key-id pithyx-release-1`, den nur der Owner hat.
- `publish-sdk.sh` setzt `latest` nie auf eine Vorabversion; `pithyx-release publish-sdk --latest` hängt `npm dist-tag add <paket>@<version> latest` für alle drei Pakete an, mit demselben `--otp`.

Schritte, die einen bestimmten Rechner brauchen, bleiben dort: Images brauchen mmdebstrap und die Build-Pakete (eine Dev-VM), Stable-Signaturen den Release-Schlüssel. Wie du eine Box vor dem Veröffentlichen aus einer Datei aktualisierst, steht unter [Update-Quelle wechseln](/de/docs/install/update-source/).
