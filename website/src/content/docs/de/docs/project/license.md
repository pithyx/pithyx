---
title: 'Lizenz'
description: 'AGPL-3.0 für das System mit einer Urheberangabe für Forks, MIT für die npm-Pakete und die Hinweise zu Drittanbietern.'
status: ready
sidebar:
  order: 1
---

Das Pithyx-System (Daemon, Desktop, Installer, Image-Werkzeuge und diese Webseite) steht unter der GNU Affero General Public License Version 3. Die drei npm-Pakete für App-Entwickler, `@pithyx/sdk`, `@pithyx/ui` und `@pithyx/cli`, stehen unter der MIT-Lizenz.

## Das System

Die Datei `LICENSE` im Repository ist die GNU AGPL v3. Die Datei `NOTICE` ergänzt drei Bedingungen nach Abschnitt 7 dieser Lizenz: eine Urheberangabe (7(b)), die Kennzeichnung geänderter Versionen (7(c)) und keine Rechte an Name und Logo (7(e)).

## Urheberangabe in Forks und Weitergaben

Pithyx wurde ursprünglich von [Schecher1](https://github.com/Schecher1) entwickelt. Jeder darf es unter der AGPL forken, ändern und weitergeben. Kopien und geänderte Versionen behalten diese Urheberangabe:

> Based on Pithyx (https://github.com/pithyx/pithyx), originally created by Schecher1 (https://github.com/Schecher1), licensed under the GNU AGPL-3.0.

- **Quellcode und Dokumentation**: die Datei `NOTICE` und überall, wo die Urheberangabe im Quellcode und in der Dokumentation steht, etwa in der README und auf dieser Seite.
- **Benutzeroberfläche**: die App **Über Pithyx** (oder eine gleichwertige, gut sichtbare Stelle); eine getreue Übersetzung ist erlaubt, so wie der Desktop sie auf Deutsch zeigt.

## Name und Logo

Die Lizenz gibt keine Rechte am Namen „Pithyx“ oder am Pithyx-Logo als Marke. Eine geänderte Version, die weitergegeben wird, braucht einen eigenen Produktnamen und darf das Pithyx-Logo nicht verwenden; sie darf sich nicht als Pithyx oder als Release des Projekts ausgeben und sagt, dass sie auf Pithyx basiert (die Urheberangabe oben). Unveränderte Kopien von Pithyx unter seinem Namen weiterzugeben ist in Ordnung. Diese Bedingungen gelten nur für das System, nicht für die npm-Pakete unter MIT.

## Die npm-Pakete

`sdk/`, `packages/ui` und `packages/cli` enthalten jeweils eine eigene Datei `LICENSE` (MIT, Copyright „Pithyx contributors“) und deklarieren `MIT` in ihrer `package.json`. Du kannst damit Apps unter jeder Lizenz bauen. Die Manifest- und Compose-Schemas unter `sdk/schema/` gehören zu `@pithyx/sdk`. Die Apps selbst behalten die Lizenz, die ihre Autoren wählen; Kataloge verlangen einen SPDX-Lizenzausdruck im Manifest.

## Hinweise zu Drittanbietern

Mitgeliefertes Material von Drittanbietern behält seine eigene Lizenz. Die Schriften IBM Plex Sans, Sora und JetBrains Mono stehen unter der SIL Open Font License 1.1 und werden unverändert ausgeliefert. Weitere mitgelieferte Komponenten sind unter anderem Tabler Icons, React Aria Components, PDF.js und SkiaSharp. Ihre Hinweise sind in der App **Über Pithyx** des Desktops aufgelistet, und die Lizenztexte liegen im Repository unter `desktop/public/licenses/`.
