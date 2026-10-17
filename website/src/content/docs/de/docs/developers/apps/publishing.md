---
title: 'In einem Katalog veröffentlichen'
description: 'Eine App in den offiziellen oder den Community-Katalog bringen oder einen eigenen Katalog betreiben.'
status: ready
audience: developer
sidebar:
  order: 5
---

Ein Katalog ist ein Git-Repository, das Apps für den Store auflistet. Pithyx hat zwei Projektkataloge, `pithyx/apps-official` und `pithyx/apps-community`, und jeder kann einen eigenen betreiben, erstellt aus dem Vorlagen-Repository `pithyx/catalog-template`. Eine Box klont die Kataloge, die sie nutzt, prüft sie und baut nie etwas.

## Kataloge

- **Offiziell** (`pithyx/apps-official`) ist neben dem System-Image die einzige Quelle für `org.pithyx.*`-Apps. **Community** (`pithyx/apps-community`) ist standardmäßig ebenfalls an; ein Administrator kann ihn ausschalten. Ein Administrator kann **Eigene** Kataloge hinzufügen.
- **Quelle.** Jede App liegt in `apps/<id>/` auf dem Branch `master`. Die CI baut das Frontend und die Images, schiebt die Images nach `ghcr.io/<owner>/apps/<id>/<service>:<version>` (nie überschrieben, per Digest verknüpft) und schreibt zwei Branches, die nur die CI beschreibt: `catalog-next` (der Beta-Kanal) und `catalog` (der Stable-Kanal). Der Stable-Branch wird erst nach einer manuellen Freigabe aktualisiert.
- **Dateien.** `catalog.json` im Wurzelverzeichnis benennt den Katalog. Jeder Branch enthält `index.json` und, bei Projektkatalogen, `index.json.sig`. Der Index listet pro Version Hashes und Größen von Paket, Manifest, Icon und Screenshots, die Images, die Architekturen, den Bereich `pithyx`, die `dataVersion`, die Lizenz und den Quell-Commit auf. Die Dateien liegen in `apps/<id>/<version>/`: `<id>-<version>.pithyxapp`, `pithyx.json`, `icon.svg` und `screenshots/`.
- **Sequenz.** Jede Veröffentlichung erhöht die `sequence` des Index um eins. Die Box lehnt eine niedrigere Nummer ab ([`store.catalog.sequence_old`](/de/docs/reference/errors/#store.catalog.sequence_old)).
- **Signatur.** Ed25519 über die exakten Bytes von `index.json`. Eine Box akzeptiert einen Projektkatalog, wenn mindestens eine Signatur von einem Schlüssel stammt, dem sie für den Kanal vertraut, und jede Signatur eines vertrauten Schlüssels stimmt. Unbekannte Schlüssel-IDs werden ignoriert, was eine Schlüsselrotation erlaubt. Eine fehlgeschlagene Prüfung ist [`store.catalog.signature_invalid`](/de/docs/reference/errors/#store.catalog.signature_invalid). Eigene Kataloge sind nicht signiert, und der Store markiert ihre Apps als nicht geprüft.
- **Auf der Box.** Kataloge sind flache Klone in `/var/lib/pithyx/catalogs/<id>/`, abgeholt mit git über https oder ssh (3 Minuten Timeout, keine Hooks, keine Submodule). Ein fehlgeschlagener Abruf behält die letzte geprüfte Kopie. Jede Datei wird gegen den Hash im Index geprüft.
- **Kanäle und Updates.** Die Store-Einstellungen haben einen Kanal (stable liest `catalog`, beta liest `catalog-next`), eine Update-Richtlinie (automatisch, benachrichtigen oder keine; Standard benachrichtigen), eine tägliche Prüfzeit (Standard 03:00 mit einer zufälligen Verzögerung von bis zu 60 Minuten) und eine Ausnahme pro App. Ein Update, das neue Berechtigungen, root oder erhöhte Rechte braucht, wird nie automatisch installiert.

## Die Prüfung

Jeder Pull Request an einen Projektkatalog wird vom Projekt geprüft: Die App-ID folgt der Regel mit der umgekehrten Domain, keine fremden Marken, kein `org.pithyx.*` im Community-Katalog, und die App muss es wert sein, aufgenommen zu werden. Wie lange eine Prüfung dauert und wie der genaue Ablauf für externe Mitwirkende aussieht, ist nicht dokumentiert.

Maschinelle Regeln laufen bei jedem Pull Request (`pithyx catalog check`, siehe unten):

- Jede App hat eine `maintainers.json` mit 1 bis 20 GitHub-Logins. Ein Pull Request, der eine App ändert, muss von einem der Maintainer kommen, wie die Datei vor der Änderung aussah. Die Datei einer neuen App muss ihren Autor nennen.
- Workflows: `check.yaml` (Pull Requests, nur lesend), `publish.yaml` (Pushes auf `master` schreiben `catalog-next`) und `promote.yaml` (manuell, schreibt `catalog` nach der Freigabe in der Umgebung `catalog-release`). Läufe von externen Mitwirkenden warten auf eine Freigabe.

## Regeln

Aus `sdk/schema/catalog.v1.rules.json`:

- Ein App-Ordner pro Pull Request, unter `apps/`. Höchstens 5 MiB pro App-Ordner und 1000 Zeichen pro Zeile.
- Pflichtdateien: `pithyx.json`, `icon.svg`, `maintainers.json` und eine von `LICENSE`, `LICENSE.md`, `LICENSE.txt`. Das Manifest braucht `license` und `pithyx`.
- Bis zu 8 Screenshots in `screenshots/`, jeder höchstens 1 MiB, benannt nach `[a-z0-9][a-z0-9_-]{0,62}` mit `.png`, `.jpg` oder `.webp`.
- Dateien sind UTF-8-Text oder aufgelistete Medien (gif, jpeg, png, webp, woff2), deren Inhalt zur Endung passt. Minifizierte Dateien (`.min.js` und ähnliche) und nicht aufgelistete Dateien werden abgelehnt.
- Jedes `FROM` und `COPY --from` in einem Dockerfile ist per `@sha256` festgelegt oder `scratch`.
- Die Lizenz ist ein SPDX-Ausdruck aus OSI-anerkannten Lizenzen; `LicenseRef-` wird abgelehnt.
- Jedes Image im Index hat jede Architektur, die es aufführt ([`store.catalog.architecture_missing`](/de/docs/reference/errors/#store.catalog.architecture_missing)), und die `dataVersion` sinkt nie.
- Builds sind so reproduzierbar wie möglich: BuildKit ist in den Workflows per Digest festgelegt, und `SOURCE_DATE_EPOCH` kommt vom letzten Commit.

## Ein eigener Katalog

1. Erstelle ein Repository aus `pithyx/catalog-template`. Der Publish-Job lehnt die Platzhalter-ID der Vorlage ab.
2. Lege deine Apps in `apps/<id>/` ab. Die CLI-Befehle laufen im Wurzelverzeichnis des Repositorys: `pithyx catalog check`, `pending`, `index`, `sign` und `verify` (siehe [@pithyx/cli](/de/docs/sdk/cli/#pithyx-catalog)).
3. Öffne im Store den Tab **Eigene** und füge das Repository per URL hinzu.
   - Eine öffentliche URL `https://host/path` wird sofort abgerufen.
   - Ein privates Repository nutzt eine SSH-URL wie `git@host:owner/repo.git`. Die Box erstellt einen SSH-Deploy-Key und zeigt dessen öffentlichen Teil; füge ihn dem Repository als schreibgeschützten Schlüssel hinzu und rufe dann ab.
   - Images in einer privaten Registry brauchen ein Lese-Token (`user:token`). Der Store hat dafür kein Feld; setze es über die API mit `PATCH /api/store/catalogs/{key}` und `registryToken`.
4. URLs nutzen https ohne Zugangsdaten oder ssh-Formen (`user@host:path`, `ssh://user@host[:port]/path`). Alles andere ist [`store.catalog.url_invalid`](/de/docs/reference/errors/#store.catalog.url_invalid).

Eigene Kataloge dürfen Images aus jeder Registry einbinden, per Digest, und folgen der globalen Update-Richtlinie. Signieren ist optional: Der Publish-Workflow signiert nur, wenn das Repository `SIGNING_KEY` definiert. Die Box prüft Signaturen nur bei den Projektkatalogen.

Pakete, die ein Workflow in die Container-Registry von GitHub schiebt, sind zunächst privat, auch aus einem öffentlichen Repository; mach jedes Paket in der Weboberfläche der Registry öffentlich, sonst können Boxen die Images nicht anonym ziehen.

Wird ein eigener Katalog entfernt, bleiben seine installierten Apps ohne Updates bestehen. Die Projektkataloge lassen sich ausschalten, aber nicht entfernen.
