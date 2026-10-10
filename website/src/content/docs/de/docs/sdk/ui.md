---
title: '@pithyx/ui'
description: 'Die React-Komponenten und Tokens des Desktops, damit Apps von Dritten nativ aussehen.'
status: draft
audience: developer
sidebar:
  order: 3
---

`@pithyx/ui` ist die React-Komponentenbibliothek des Pithyx-Desktops, gebaut auf React Aria Components (Unterstützung für Tastatur, Touch und Screenreader), zusammen mit den Design-Tokens in `tokens.css`. Die Tokens und die Klassennamen für einfache HTML-Elemente funktionieren ohne React. Apps von Dritten können es nutzen, um wie der Desktop auszusehen; das SDK setzt es nicht voraus.

## Installieren

```sh
npm install @pithyx/ui@next react react-dom
```

React und React DOM 19 sind Peer-Abhängigkeiten. Importiere die Token-Datei einmal und umgib deine App mit dem Provider:

```tsx
import '@pithyx/ui/tokens.css';
import { UiProvider, Button } from '@pithyx/ui';

export function App() {
  return (
    <UiProvider locale="de-AT">
      <Button>Save</Button>
    </UiProvider>
  );
}
```

`UiProvider` setzt die Sprache für React Aria und für die eigenen Texte des Pakets (Englisch und Deutsch; andere Sprachen zeigen Englisch). Ohne ihn folgen die Komponenten dem Browser.

## Tokens ohne React

`@pithyx/ui/tokens.css` ist die einzige Token-Datei von Pithyx und braucht kein JavaScript. Die Farben folgen der hellen oder dunklen Einstellung des Systems (`light-dark()`); erzwinge eine mit `<html data-theme="light">` oder `"dark"`. Wähle eine Akzentfarbe mit `<html data-accent="...">`: `petrol` (Standard), `ocean`, `indigo`, `plum`, `moss` oder `graphite`. Jede Voreinstellung ist in beiden Themes auf Kontrast geprüft. Die Token-Namen stehen unter [Themes und Design-Tokens](/de/docs/developers/desktop/theming/).

Die Komponenten lesen die Tokens `--color-*`, `--space-*`, `--radius-*` und `--control-height`, nicht die `--pithyx-*`-Eigenschaften des SDK. Eine App, die die Komponenten nutzt, importiert deshalb `tokens.css`.

`@pithyx/ui/native` exportiert Klassennamen für native Elemente, die wie die Komponenten aussehen, ohne React Aria: `buttonStyles`, `controlStyles` und `textFieldStyles`, dazu `cx` und `uiTexts`. Es ist für Code gedacht, der klein bleiben muss.

## Komponenten

- Buttons: `Button` (mit `ButtonVariant`) und `IconButton`.
- Felder: `TextField`, `PasswordField`, `SearchField` und `OtpField`.
- Steuerelemente: `Checkbox`, `Switch`, `ProgressBar` und `Badge`.
- Überlagerungen: `Dialog`, `Menu`, `MenuItem`, `MenuTrigger`, `MenuPopover`, `MenuSeparator`, `ContextMenuPopover`, `Tooltip` und `TooltipTrigger`.
- Helfer: `UiProvider`, `useDeliberatePress`, `cx` und `uiTexts`.

Die Komponenten sind durch ihre TSDoc-Kommentare dokumentiert; `pnpm docs:api` im Repository baut die TypeDoc-Referenz. Hier gibt es keine Seite pro Komponente.
