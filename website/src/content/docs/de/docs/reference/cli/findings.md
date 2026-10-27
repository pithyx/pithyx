---
title: 'Befunde'
description: 'Jeder Befund, den der Befehl pithyx melden kann, mit Stufe und Meldung, erzeugt aus der CLI und den Übersetzungen der Box.'
status: ready
audience: developer
sidebar:
  order: 11
---

Ein Befund ist ein Problem, das der Befehl `pithyx` meldet, auf der Standardfehlerausgabe als `<level> <code>: <message>`. Ein Fehler stoppt [`pack`](/de/docs/reference/cli/pack/) und lässt den Befehl mit 1 enden; eine Warnung nicht. Die meisten Codes sind die Fehlercodes der Box mit der Meldung der Box, die CLI sagt also, was die Box sagen würde; die Codes in `cli.*` gibt es nur in der CLI, weil die Box ein Paket sieht und nicht das Projekt, aus dem es gebaut wurde. Diese Liste wird aus `packages/cli/src/issues.ts`, `findings.ts` und `i18n/<locale>/errors.json` erzeugt. Die CLI selbst gibt die Meldungen immer englisch aus; hier stehen die deutschen Texte der Box, die `cli.*`-Meldungen gibt es nur englisch. `{name}` markiert einen Parameter; hängt eine Meldung von einem Parameter ab, folgen ihre Varianten darunter.

## App-Pakete

Gemeldet von [`validate`](/de/docs/reference/cli/validate/) und [`pack`](/de/docs/reference/cli/pack/), die Compose-Codes auch von [`build`](/de/docs/reference/cli/build/), und von [`catalog check`](/de/docs/reference/cli/catalog-check/) und [`catalog index`](/de/docs/reference/cli/catalog-index/) für die Apps eines Katalogs.

<!-- generated:cli-findings app -->

- [`app.compose.key_unsupported`](/de/docs/reference/errors/#app.compose.key_unsupported) (Fehler): Die Compose-Datei der App nutzt {key} unter {path}, was Pithyx nicht erlaubt.
- [`app.compose.reference_unknown`](/de/docs/reference/errors/#app.compose.reference_unknown) (Fehler): Das Backend der App verweist unter {path} auf {name}, das nicht deklariert ist.
- [`app.compose.right_missing`](/de/docs/reference/errors/#app.compose.right_missing) (Fehler): Die Compose-Datei der App braucht unter {path} das Recht {right}, das ihr Manifest nicht deklariert.
- [`app.compose.right_unused`](/de/docs/reference/errors/#app.compose.right_unused) (Fehler): Das Manifest der App deklariert unter {path} das Recht {right}, das ihre Compose-Datei nicht nutzt.
- [`app.compose.storage_mismatch`](/de/docs/reference/errors/#app.compose.storage_mismatch) (Fehler): Der Speicher {storage} passt nicht zur Compose-Datei unter {path}: {reason}.
  - `reason` = `not_mounted`: der Dienst bindet ihn nicht ein
  - `reason` = `not_listed`: der Dienst bindet ihn ein, ohne im Manifest genannt zu sein
  - `reason` = `read_only`: er ist schreibgeschützt und muss mit :ro eingebunden werden
  - `reason` = `other`: Manifest und Compose-Datei widersprechen sich
- [`app.compose.value_invalid`](/de/docs/reference/errors/#app.compose.value_invalid) (Fehler): Die Compose-Datei der App hat unter {path} einen Wert, den Pithyx ablehnt: {reason}.
  - `reason` = `required`: er fehlt
  - `reason` = `type`: er hat den falschen Typ
  - `reason` = `format`: sein Format ist ungültig
  - `reason` = `range`: er liegt außerhalb des erlaubten Bereichs
  - `reason` = `unpinned`: das Image ist nicht mit @sha256 festgelegt
  - `reason` = `interpolation`: er nutzt $ für eine Variable (schreib $$ für ein Dollarzeichen)
  - `reason` = `no_value`: die Variable hat keinen Wert
  - `reason` = `duplicate`: er kommt doppelt vor
  - `reason` = `reserved`: er ist für Pithyx reserviert
  - `reason` = `host_path`: er bindet einen Ordner des Hosts ein
  - `reason` = `anonymous`: das Volume hat keinen Namen
  - `reason` = `cycle`: die Dienste hängen im Kreis voneinander ab
  - `reason` = `image_and_build`: der Dienst hat image und build, nutze nur eins davon
  - `reason` = `other`: er ist ungültig
- [`app.compose.yaml_invalid`](/de/docs/reference/errors/#app.compose.yaml_invalid) (Fehler): Die Compose-Datei der App ist nicht lesbar: {reason}.
  - `reason` = `too_large`: sie ist größer als 64 KB
  - `reason` = `not_utf8`: sie ist kein UTF-8
  - `reason` = `character`: sie enthält einen Tabulator oder ein anderes nicht erlaubtes Zeichen
  - `reason` = `syntax`: sie ist kein gültiges YAML
  - `reason` = `documents`: sie muss genau ein Dokument enthalten
  - `reason` = `too_deep`: sie ist zu tief verschachtelt
  - `reason` = `directive`: sie enthält eine YAML-Direktive
  - `reason` = `anchor`: sie nutzt Anker oder Aliase
  - `reason` = `tag`: sie nutzt YAML-Tags
  - `reason` = `duplicate_key`: {path} kommt doppelt vor
  - `reason` = `merge_key`: sie nutzt unter {path} einen Merge-Schlüssel
  - `reason` = `key_not_string`: ein Schlüssel unter {path} ist kein Text
  - `reason` = `other`: sie verletzt eine Regel für Compose-Dateien
- [`app.elevated.backend_missing`](/de/docs/reference/errors/#app.elevated.backend_missing) (Fehler): Die App verlangt {right}, ein Recht, das nur ein Hintergrunddienst nutzen kann, aber sie hat keinen Hintergrunddienst.
- [`app.elevated.root_conflict`](/de/docs/reference/errors/#app.elevated.root_conflict) (Fehler): Die App führt ihren Hintergrunddienst als root aus und verlangt {right}. Mit diesem Recht läuft der Dienst direkt auf der Box, wo root die ganze Box beherrschen würde, deshalb lehnt Pithyx die Kombination ab.
- [`app.elevated.root_required`](/de/docs/reference/errors/#app.elevated.root_required) (Fehler): Die App verlangt {right}, aber nur ein Hintergrunddienst, der als root läuft, kann eine Capability nutzen.
- [`app.elevated.unsupported`](/de/docs/reference/errors/#app.elevated.unsupported) (Fehler): Die App verlangt das Recht {right}, das Pithyx keiner App gibt.
- [`app.icon.invalid`](/de/docs/reference/errors/#app.icon.invalid) (Fehler): Das Symbol der App ist nicht verwendbar: {reason}.
  - `reason` = `too_large`: es ist größer als 64 KB
  - `reason` = `malformed`: es ist keine gültige SVG-Datei
  - `reason` = `not_svg`: es ist kein SVG-Bild
  - `reason` = `viewbox`: seine viewBox fehlt oder ist nicht quadratisch
  - `reason` = `doctype`: es enthält einen DOCTYPE
  - `reason` = `processing_instruction`: es enthält eine Verarbeitungsanweisung
  - `reason` = `element_forbidden`: es enthält das Element {element}
  - `reason` = `event_attribute`: es enthält das Ereignisattribut {attribute}
  - `reason` = `external_href`: es verweist auf etwas außerhalb der Datei
  - `reason` = `style_import`: seine Styles nutzen @import
  - `reason` = `style_url`: es lädt mit url() etwas außerhalb der Datei
  - `reason` = `style_escape`: seine Styles enthalten Escape-Sequenzen
  - `reason` = `other`: es verletzt eine Regel für Symbole
- [`app.manifest.cron_invalid`](/de/docs/reference/errors/#app.manifest.cron_invalid) (Fehler): Der Zeitplan unter {path} ist kein gültiger Cron-Ausdruck: {cron}
- [`app.manifest.duplicate`](/de/docs/reference/errors/#app.manifest.duplicate) (Fehler): Das Manifest enthält {value} doppelt (unter {path}).
- [`app.manifest.id_invalid`](/de/docs/reference/errors/#app.manifest.id_invalid) (Fehler): Die App-ID {id} ist ungültig. Sie besteht aus der umgekehrten Domain des Autors und dem App-Namen, zum Beispiel com.example.chat.
- [`app.manifest.id_reserved`](/de/docs/reference/errors/#app.manifest.id_reserved) (Fehler): Die App-ID {id} ist für die Apps reserviert, die mit Pithyx kommen.
- [`app.manifest.invalid_json`](/de/docs/reference/errors/#app.manifest.invalid_json) (Fehler): Das Manifest der App (pithyx.json) ist kein gültiges JSON.
- [`app.manifest.permission_missing`](/de/docs/reference/errors/#app.manifest.permission_missing) (Fehler): Das Manifest nutzt {path}, das die Berechtigung {permission} braucht. Ergänze sie unter permissions.
- [`app.manifest.pithyx_invalid`](/de/docs/reference/errors/#app.manifest.pithyx_invalid) (Fehler): Der Pithyx-Versionsbereich {pithyx} im Manifest ist ungültig.
- [`app.manifest.reference_unknown`](/de/docs/reference/errors/#app.manifest.reference_unknown) (Fehler): Das Manifest verweist unter {path} auf {name}, das es nicht deklariert.
- [`app.manifest.schema_violation`](/de/docs/reference/errors/#app.manifest.schema_violation) (Fehler): Das Manifest der App ist unter {path} ungültig ({keyword}).
- [`app.manifest.sdk_invalid`](/de/docs/reference/errors/#app.manifest.sdk_invalid) (Fehler): Der SDK-Versionsbereich {sdk} im Manifest ist ungültig.
- [`app.manifest.version_unsupported`](/de/docs/reference/errors/#app.manifest.version_unsupported) (Fehler): {version}
  - `version` = `1`: Diese App wurde für eine ältere Version von Pithyx gemacht (Manifest-Version 1, diese Box liest Version {supported}). Bitte ihren Autor um eine neue Version.
  - `version` = `other`: Diese App braucht eine neuere Version von Pithyx (Manifest-Version {version}, diese Box liest Version {supported}).
- [`app.manifest.window_size_invalid`](/de/docs/reference/errors/#app.manifest.window_size_invalid) (Fehler): Die kleinste Fenstergröße der App ist größer als ihre Standardgröße.
- [`app.package.invalid`](/de/docs/reference/errors/#app.package.invalid) (Fehler): Die Datei ist kein gültiges App-Paket: {reason}.
  - `reason` = `gzip`: sie ist nicht mit gzip komprimiert oder beschädigt
  - `reason` = `format`: sie ist kein tar-Archiv, das diese Box liest
  - `reason` = `checksum`: ein Header ist beschädigt
  - `reason` = `truncated`: sie endet zu früh
  - `reason` = `trailing_data`: nach ihrem Ende folgen noch Daten
  - `reason` = `entry_type`: {path} ist ein Link oder eine Spezialdatei
  - `reason` = `extended_header`: sie hat einen erweiterten Header, der nicht erlaubt ist
  - `reason` = `name_mismatch`: der lange Name von {path} passt nicht zu seinem eigenen Namen
  - `reason` = `other`: sie kann nicht gelesen werden
- [`app.package.layout_invalid`](/de/docs/reference/errors/#app.package.layout_invalid) (Fehler): Das App-Paket ist ungültig: {reason}.
  - `reason` = `missing`: {path} fehlt
  - `reason` = `unsafe`: der Pfad {path} ist nicht erlaubt
  - `reason` = `duplicate`: {path} ist doppelt enthalten
  - `reason` = `unexpected`: {path} gehört nicht in ein App-Paket
  - `reason` = `reserved`: {path} nutzt einen Namen, der für das Backend reserviert ist
  - `reason` = `other`: {path} ist nicht erlaubt

<!-- /generated -->

## Projekte

Nur die CLI meldet diese. `cli.backend.unbuilt` kommt vom Vite-Plugin nach `vite build`, für eine App, deren Backend bisher nur seine Quellform `compose.dev.yaml` hat, wie in einem Katalog, bevor seine CI die Images baut.

<!-- generated:cli-findings cli -->

- `cli.backend.unbuilt` (Warnung): {path} is not built yet: run pithyx build, or let the catalog build it from the source form.
- `cli.compose.healthcheck_redundant` (Warnung): Service {service} has a Compose healthcheck next to the health of the manifest. Docker starts a process in the container for every check, which delays every start and keeps the idle backend busy. Remove it and let health decide.
- `cli.frontend.missing` (Fehler): The frontend build {folder} does not exist. Build the app first, or name the folder with --frontend.
- `cli.frontend.skipped` (Warnung): {path} is left out of the package: hidden files and folders cannot be in an app package.
- `cli.icon.template` (Warnung): icon.svg is still the template icon (the Pithyx box). Replace it with the icon of your app.
- `cli.lock.invalid` (Fehler): {path} is not a valid images lock. Run pithyx lock to write it again.
- `cli.lock.missing` (Fehler): {path} is missing. Run pithyx lock to resolve the images of the backend.
- `cli.lock.platform_missing` (Warnung): {image} has no image for {arch}; the app will not install on {arch} boxes.
- `cli.lock.stale` (Fehler): {path} does not match the Compose file ({reason}). Run pithyx lock.
  - `reason` = `missing`: {image} is not locked
  - `reason` = `unused`: {image} is not used
  - `reason` = `other`: {image} differs
- `cli.manifest.missing` (Fehler): pithyx.json does not exist in {folder}. Run the command in the project folder of an app, or name that folder.
- `cli.rights.raw_socket` (Warnung): The app asks for docker.socket.raw, the unfiltered container service: root on the box. Every box asks the administrator to type the app's name to grant it, and the app never updates by itself. If the app only manages containers it starts itself, ask for docker.socket instead.

<!-- /generated -->

## Kataloge

Gemeldet von den [`catalog`](/de/docs/reference/cli/catalog-check/)-Befehlen.

<!-- generated:cli-findings store -->

- [`store.catalog.architecture_missing`](/de/docs/reference/errors/#store.catalog.architecture_missing) (Fehler): Der Katalog führt unter {path} ein Image ohne die Architektur {architecture}.
- [`store.catalog.data_version_decreased`](/de/docs/reference/errors/#store.catalog.data_version_decreased) (Fehler): Der Katalog senkt unter {path} die Datenversion auf {dataVersion}, nach {previous} in einer älteren Version.
- [`store.catalog.id_reserved`](/de/docs/reference/errors/#store.catalog.id_reserved) (Fehler): Der Katalog nutzt unter {path} die ID {id}, die nur der offizielle Pithyx-Katalog nutzen darf.
- [`store.catalog.invalid_json`](/de/docs/reference/errors/#store.catalog.invalid_json) (Fehler): Die Datei {file} des Katalogs ist kein gültiges JSON.
- [`store.catalog.mismatch`](/de/docs/reference/errors/#store.catalog.mismatch) (Fehler): Der Index des Katalogs hat unter {path} den Wert {value}, diese Box erwartet aber {expected}.
- [`store.catalog.rule_violation`](/de/docs/reference/errors/#store.catalog.rule_violation) (Fehler): Der Pull Request verletzt eine Regel des Katalogs: {rule}.
  - `rule` = `one_app`: {path} liegt außerhalb des einen App-Ordners, den ein Pull Request ändern darf
  - `rule` = `required_file`: {path} fehlt
  - `rule` = `file_type`: {path} ist eine Dateiart, die ein Katalog nicht annimmt
  - `rule` = `binary`: {path} ist binär oder kein UTF-8
  - `rule` = `minified`: {path} sieht minifiziert oder generiert aus
  - `rule` = `media`: {path} ist nicht das Bild oder die Schrift, die der Name angibt
  - `rule` = `unpinned`: Zeile {line} von {path} nutzt das Image {image}, das nicht mit @sha256 festgelegt ist
  - `rule` = `screenshot`: {path} ist kein Screenshot direkt in screenshots/ als PNG-, JPEG- oder WebP-Datei mit kleingeschriebenem Namen und höchstens 1 MB
  - `rule` = `screenshots`: {path} enthält mehr als {limit} Screenshots
  - `rule` = `app_size`: {path} ist größer als {limit} Byte
  - `rule` = `folder_id`: der Ordner von {path} muss wie die ID der App heißen: {id}
  - `rule` = `manifest_field`: das Manifest {path} braucht das Feld {field}
  - `rule` = `license`: {license} in {path} ist keine von der OSI anerkannte Lizenz
  - `rule` = `version_not_higher`: Version {version} in {path} ist nicht höher als die veröffentlichte {published}
  - `rule` = `maintainer`: {author} gehört nicht zu den Maintainern der App
  - `rule` = `maintainers_invalid`: {path} ist keine gültige Liste von Maintainern
  - `rule` = `other`: {path} verletzt eine Regel
- [`store.catalog.schema_violation`](/de/docs/reference/errors/#store.catalog.schema_violation) (Fehler): Die Datei {file} des Katalogs ist unter {path} ungültig ({keyword}).
- [`store.catalog.sequence_old`](/de/docs/reference/errors/#store.catalog.sequence_old) (Fehler): Der Index des Katalogs ist älter (Sequenz {sequence}) als einer, den diese Box schon geprüft hat ({known}). Die Box behält ihre Kopie.
- [`store.catalog.signature_invalid`](/de/docs/reference/errors/#store.catalog.signature_invalid) (Fehler): Der Index des Katalogs ist nicht korrekt signiert ({reason}). Die Box behält ihre zuletzt geprüfte Kopie.
  - `reason` = `missing`: die Signatur fehlt
  - `reason` = `malformed`: die Signaturdatei ist ungültig
  - `reason` = `untrusted`: keine Signatur stammt von einem Schlüssel, dem diese Box für ihren Kanal vertraut
  - `reason` = `mismatch`: eine Signatur passt nicht zum Index
  - `reason` = `other`: die Signatur belegt ihn nicht
- [`store.catalog.version_unsupported`](/de/docs/reference/errors/#store.catalog.version_unsupported) (Fehler): Die Datei {file} des Katalogs hat die Formatversion {version}, diese Box liest Version {supported}. Aktualisiere Pithyx, um diesen Katalog zu nutzen.
- [`store.package.hash_mismatch`](/de/docs/reference/errors/#store.package.hash_mismatch) (Fehler): Eine Datei des Katalogs passt nicht zu seinem Index ({path}). Die Box installiert sie nicht.

<!-- /generated -->
