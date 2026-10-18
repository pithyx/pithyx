---
title: 'Veröffentlichen'
description: 'Die Schritte eines Releases und das Release-Werkzeug tools/release/pithyx-release: ISO und Update-Dateien bauen, signieren und mit den npm-Paketen veröffentlichen.'
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

## Eine Version veröffentlichen

Die Schritte eines Releases `X.Y.Z`, der Reihe nach. Bauen und Testen laufen auf einer Dev-VM; das Mergen nach `beta` und `stable`, der Tag, die Stable-Signaturen und das Veröffentlichen auf npm sind Sache des Maintainers.

1. **Branches.** Die Änderungen liegen auf `dev` mit grüner CI, einschließlich eines E2E-Laufs (`gh workflow run CI --ref dev`). Ein Pull Request `dev` → `beta`, dann `beta` → `stable`; beide brauchen grüne CI (das Ruleset `protected-branches`).
2. **Version.** Die Version des Daemons in `daemon/Directory.Build.props`, der Abschnitt der Version in `CHANGELOG.md` und die Doku (`since`, Versionsnummern) sind vor Schritt 1 auf `dev`.
3. **Image.** Auf der Dev-VM, vom Commit auf `stable`: `pithyx-release iso --version X.Y.Z`. Danach installiert `image/test-install.sh --iso image/out/pithyx-X.Y.Z-amd64.iso --mirror` es in QEMU und prüft das installierte System.
4. **Update-Datei.** `pithyx-release update --version X.Y.Z` baut die `.pithyxupdate` gegen die Baseline (`image/baseline/`), und `pithyx-release baseline --version X.Y.Z` bewahrt die neue Paketliste für das nächste Update auf (committe sie auf `dev`).
5. **GPL-Quellen.** `pithyx-release sources --version X.Y.Z` packt die Debian-Quellen des Images als `pithyx-X.Y.Z-sources-<n>.tar`.
6. **Tag.** Der Maintainer taggt den Commit, aus dem das Image gebaut wurde: `git tag -a vX.Y.Z <commit> -m "Pithyx X.Y.Z <Codename>"`, `git push origin vX.Y.Z`.
7. **Signieren (Maintainer, eigener Rechner).** `pithyx-release sign <datei> --key <pithyx-release-1.pem> --key-id pithyx-release-1`, dann `pithyx-release channel <datei> --key <pem> --key-id pithyx-release-1 --sequence <n>` mit der Sequenz von `channel-stable` plus eins. Der Release-Schlüssel verlässt nie den Rechner des Maintainers; das ISO selbst trägt keine Signatur, nur seine Prüfsumme.
8. **Veröffentlichen.** Das Release `vX.Y.Z` mit dem ISO, seiner `.sha256`, der Update-Datei mit ihrer `.sha256` und den Quell-Teilen (`gh release create vX.Y.Z --verify-tag ...`); dann `channel.json` und `channel.json.sig` am Release `channel-stable` (`gh release upload channel-stable ... --clobber`). Boxen auf dem Stable-Kanal finden das Update bei ihrer nächsten täglichen Prüfung und benachrichtigen ihre Administratoren.
9. **Website.** Pushes auf `stable` veröffentlichen die Doku; Actions, Pages, Run workflow auf `stable` veröffentlicht von Hand.
10. **npm (wenn sich die Pakete geändert haben).** `pithyx-release publish-sdk`, für eine stabile Version mit `--latest`. Mit einem Sicherheitsschlüssel statt Einmalcodes lässt du `--otp` weg: npm zeigt einen Link, den du im Browser bestätigst.

Eine Beta-Version (`X.Y.Z-beta.N`) geht denselben Weg bis `beta` und endet dort: `pithyx-release update --version X.Y.Z-beta.N --sign` signiert mit dem Beta-Schlüssel, und `pithyx-release publish-beta <datei>` veröffentlicht sie auf `beta-X.Y.Z-beta.N` und `channel-beta`.
