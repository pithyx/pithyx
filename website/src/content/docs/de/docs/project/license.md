---
title: 'Lizenz'
description: 'AGPL-3.0 für das System, MIT für die npm-Pakete und die Hinweise zu Drittanbietern.'
status: ready
sidebar:
  order: 1
---

Das Pithyx-System (Daemon, Desktop, Installer, Image-Werkzeuge und diese Webseite) steht unter der GNU Affero General Public License Version 3. Die drei npm-Pakete für App-Entwickler, `@pithyx/sdk`, `@pithyx/ui` und `@pithyx/cli`, stehen unter der MIT-Lizenz.

## Das System

Die Datei `LICENSE` im Repository ist die GNU AGPL v3. Die Datei `NOTICE` ergänzt Bedingungen nach Abschnitt 7 dieser Lizenz: Die Benutzeroberfläche muss den Hinweistext in der About-App beibehalten („Based on Pithyx (https://github.com/pithyx/pithyx), created by the Pithyx contributors and licensed under the GNU AGPL-3.0.“), und geänderte Versionen müssen als geändert gekennzeichnet werden. Die Datei `NOTICE` wurde noch von keinem Anwalt geprüft.

## Die npm-Pakete

`sdk/`, `packages/ui` und `packages/cli` enthalten jeweils eine eigene Datei `LICENSE` (MIT, Copyright „Pithyx contributors“) und deklarieren `MIT` in ihrer `package.json`. Du kannst damit Apps unter jeder Lizenz bauen. Die Manifest- und Compose-Schemas unter `sdk/schema/` gehören zu `@pithyx/sdk`. Die Apps selbst behalten die Lizenz, die ihre Autoren wählen; Kataloge verlangen einen SPDX-Lizenzausdruck im Manifest.

## Hinweise zu Drittanbietern

Mitgeliefertes Material von Drittanbietern behält seine eigene Lizenz. Die Schriften IBM Plex Sans, Sora und JetBrains Mono stehen unter der SIL Open Font License 1.1 und werden unverändert ausgeliefert. Weitere mitgelieferte Komponenten sind unter anderem Tabler Icons, React Aria Components, PDF.js und SkiaSharp. Ihre Hinweise sind in der App **Über Pithyx** des Desktops aufgelistet, und die Lizenztexte liegen im Repository unter `desktop/public/licenses/`.
