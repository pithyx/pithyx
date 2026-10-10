---
title: 'Lizenz'
description: 'AGPL-3.0 für das System mit einer Urheberangabe für Forks, MIT für die npm-Pakete und die Hinweise zu Drittanbietern.'
status: ready
sidebar:
  order: 1
---

Das Pithyx-System (Daemon, Desktop, Installer, Image-Werkzeuge und diese Webseite) steht unter der GNU Affero General Public License Version 3. Die drei npm-Pakete für App-Entwickler, `@pithyx/sdk`, `@pithyx/ui` und `@pithyx/cli`, stehen unter der MIT-Lizenz.

## Das System

Die Datei `LICENSE` im Repository ist die GNU AGPL v3. Die Datei `NOTICE` ergänzt Bedingungen nach Abschnitt 7(b) dieser Lizenz, der erlaubt, die Erhaltung bestimmter angemessener rechtlicher Hinweise oder Urheberangaben zu verlangen („requiring preservation of specified reasonable legal notices or author attributions“). Die Datei `NOTICE` wurde noch von keinem Anwalt geprüft.

## Urheberangabe in Forks und Weitergaben

Pithyx wurde ursprünglich von [Schecher1](https://github.com/Schecher1) entwickelt. Jeder darf es unter der AGPL forken, ändern und weitergeben, aber ein Fork oder eine Weitergabe darf weder den Namen Pithyx noch die Herkunft von Schecher1 verbergen. Er behält diese Urheberangabe:

> Based on Pithyx (https://github.com/pithyx/pithyx), originally created by Schecher1 (https://github.com/Schecher1), licensed under the GNU AGPL-3.0.

- **Quellcode**: die Datei `NOTICE` mit der Urheberangabe und ihren Bedingungen sowie die README.
- **Benutzeroberfläche**: die App **Über Pithyx** (oder eine gleichwertige, gut sichtbare Stelle); eine getreue Übersetzung ist erlaubt, so wie der Desktop sie auf Deutsch zeigt.
- **Dokumentation**: überall, wo das abgeleitete Werk seine Lizenz, Herkunft oder Urheber beschreibt.

Ein Fork darf einen eigenen Produktnamen, ein eigenes Logo und eigenes Branding verwenden, aber wo er sich an diesen Stellen nennt, sagt er, dass er auf Pithyx von Schecher1 basiert, und er kennzeichnet sich als geändert (Abschnitt 5(a) der AGPL). Diese Bedingungen gelten nur für das System, nicht für die npm-Pakete unter MIT.

## Die npm-Pakete

`sdk/`, `packages/ui` und `packages/cli` enthalten jeweils eine eigene Datei `LICENSE` (MIT, Copyright „Pithyx contributors“) und deklarieren `MIT` in ihrer `package.json`. Du kannst damit Apps unter jeder Lizenz bauen. Die Manifest- und Compose-Schemas unter `sdk/schema/` gehören zu `@pithyx/sdk`. Die Apps selbst behalten die Lizenz, die ihre Autoren wählen; Kataloge verlangen einen SPDX-Lizenzausdruck im Manifest.

## Hinweise zu Drittanbietern

Mitgeliefertes Material von Drittanbietern behält seine eigene Lizenz. Die Schriften IBM Plex Sans, Sora und JetBrains Mono stehen unter der SIL Open Font License 1.1 und werden unverändert ausgeliefert. Weitere mitgelieferte Komponenten sind unter anderem Tabler Icons, React Aria Components, PDF.js und SkiaSharp. Ihre Hinweise sind in der App **Über Pithyx** des Desktops aufgelistet, und die Lizenztexte liegen im Repository unter `desktop/public/licenses/`.
