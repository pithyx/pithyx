---
title: 'SDK im Überblick'
description: 'Die drei npm-Pakete, ihre Versionen und wie sie zusammenspielen.'
status: draft
audience: developer
sidebar:
  order: 1
---

Drei npm-Pakete teilen sich eine Versionslinie und eine MIT-Lizenz: `@pithyx/sdk` ist die API zwischen einer App und dem Desktop, `@pithyx/ui` sind die React-Komponenten und Design-Tokens des Desktops, und `@pithyx/cli` ist der Befehl `pithyx`, der Apps prüft, entwickelt und paketiert. Das System selbst steht unter AGPL-3.0.

## Pakete

| Paket         | Was es ist                                                                                      | Braucht React |
| ------------- | ----------------------------------------------------------------------------------------------- | ------------- |
| `@pithyx/sdk` | `connect()` und die API `PithyxApp`; JSON-Schemas für Manifeste, Compose-Regeln und Kataloge    | nein          |
| `@pithyx/ui`  | Buttons, Felder, Menüs, Dialoge und `tokens.css`, gebaut auf React Aria Components              | ja (19)       |
| `@pithyx/cli` | Der Befehl `pithyx` (`validate`, `lock`, `build`, `pack`, `dev`, `catalog`) und ein Vite-Plugin | nein          |

- **`@pithyx/sdk`** hat den Einstiegspunkt `.`, den Unterpfad `./i18n` (ein kleiner Meldungs-Helfer für Apps) und `./host` (die Seite des Desktops, nicht für Apps). Die Schemas liegen unter `@pithyx/sdk/schema/`. Es funktioniert mit jedem Framework oder reinem HTML. Siehe [@pithyx/sdk](/de/docs/sdk/sdk/).
- **`@pithyx/ui`** exportiert die Komponenten, `@pithyx/ui/native` (Klassennamen für einfache HTML-Elemente) und `@pithyx/ui/tokens.css`. Siehe [@pithyx/ui](/de/docs/sdk/ui/).
- **`@pithyx/cli`** exportiert die Validatoren als JavaScript-API, `@pithyx/cli/vite` und ein Vorlagen-Icon. Es braucht Node.js 22.12 oder neuer. Siehe [@pithyx/cli](/de/docs/sdk/cli/).

Core-Apps und Apps von Dritten nutzen dieselbe SDK-API. Core-Apps verbinden sich im selben Prozess, Apps von Dritten über einen `MessageChannel` aus ihrem Sandbox-iframe.

## Versionen und Kanäle

Alle drei Pakete tragen dieselbe Version, unabhängig von der Pithyx-Version. Es sind Release Candidates von 1.0.0 (`1.0.0-rc.<n>`), deren API sich vor 1.0.0 noch ändern kann. Release Candidates werden unter dem npm-Dist-Tag `next` veröffentlicht.

Kompatibilitätsregeln (aus `sdk/src/version.ts`):

- Eine neue Methode, ein neues Ereignis oder ein neues optionales Feld ist eine Minor-Änderung; eines zu entfernen oder zu ändern ist eine Major-Änderung.
- Ein veraltetes Feature funktioniert bis durch die nächste Major-Version weiter.
- Ein Desktop der Major-Version N+1 liefert weiterhin Apps aus, die für N gebaut wurden.
- Das Wire-Protokoll hat eine eigene Version (`PROTOCOL_VERSION`, derzeit 1). Ergänzungen innerhalb einer Version findest du über `app.supports(method)` und `app.context.capabilities`.
- Das Manifest-Schema, die Compose-Regeln und die Katalogformate sind stabile Verträge: Lockern ist eine Minor-Änderung, alles andere eine Major-Änderung.

## Installieren

```sh
npm install @pithyx/sdk@next
npm install @pithyx/ui@next react react-dom
npm install --save-dev @pithyx/cli@next
```

Die Pakete liefern TypeScript-Typen mit TSDoc-Kommentaren; im Repository baut `pnpm docs:api` mit TypeDoc eine API-Referenz von `@pithyx/sdk` und `@pithyx/ui`. Beginne mit [Deine erste App](/de/docs/developers/apps/first-app/).
