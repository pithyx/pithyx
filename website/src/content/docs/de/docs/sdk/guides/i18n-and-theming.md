---
title: 'Sprachen und Darstellung in einer App'
description: 'Texte mit @pithyx/sdk/i18n in der Sprache des Benutzers zeigen, Daten und Größen mit app.format formatieren und der Darstellung des Desktops mit CSS-Eigenschaften folgen.'
status: ready
audience: developer
sidebar:
  order: 5
---

Eine App übersetzt ihre Texte selbst: Der Desktop gibt ihr Sprache, Formate und Zeitzone des Benutzers in `app.context` und sagt, wenn sie sich ändern. `@pithyx/sdk/i18n` ist ein optionaler Helfer für Texte in ICU MessageFormat, dem Format, das auch der Desktop nutzt, und `app.format` formatiert Daten, Zahlen, Größen und Listen. Das Aussehen kommt als CSS-Eigenschaften `--pithyx-*`, die das SDK auf der Seite setzt und aktuell hält; einfaches CSS folgt so Hell und Dunkel, der Akzentfarbe und der Dichte.

## Beispiel

Die Texte liegen in einer JSON-Datei pro Sprache:

<!-- example: sdk/i18n/en.json -->

```json
{
  "files": {
    "title": "Files",
    "count": "{count, plural, one {# file} other {# files}}",
    "changed": "Changed {when}"
  }
}
```

<!-- example: sdk/i18n/de.json -->

```json
{
  "files": {
    "title": "Dateien",
    "count": "{count, plural, one {# Datei} other {# Dateien}}",
    "changed": "Geändert {when}"
  }
}
```

<!-- example: sdk/i18n-and-theming.ts -->

```ts
// Texts in the user's language, dates and sizes in the user's formats, and colors from the desktop's theme.
import { connect, type PithyxApp } from '@pithyx/sdk';
import { createI18n } from '@pithyx/sdk/i18n';
// ICU MessageFormat, like the desktop's own texts; English is the fallback for keys a language lacks.
import de from './i18n/de.json';
import en from './i18n/en.json';

const app: PithyxApp = await connect();
const i18n = createI18n(app, { en, de });

interface Row {
  name: string;
  size: number;
  modified: string;
}

function render(rows: Row[]) {
  document.title = i18n.t('files.title');
  const heading = document.createElement('h1');
  heading.textContent = `${i18n.t('files.title')} (${i18n.t('files.count', { count: rows.length })})`;
  const list = document.createElement('ul');
  for (const row of rows) {
    const item = document.createElement('li');
    // Dates arrive as UTC; app.format shows them in the user's time zone and locale.
    item.textContent = `${row.name}, ${app.format.bytes(row.size)}, ${i18n.t('files.changed', {
      when: app.format.relative(row.modified),
    })}`;
    list.append(item);
  }
  document.body.replaceChildren(heading, list);
}

const rows: Row[] = [
  { name: 'report.pdf', size: 1_234_567, modified: new Date(Date.now() - 3_600_000).toISOString() },
];

render(rows);
// The SDK already set the new lang attribute and theme properties when these handlers run.
app.on('localeChange', () => {
  render(rows);
});
app.on('themeChange', (theme) => {
  // CSS with var(--pithyx-...) follows by itself; only canvas drawings and the like need the values.
  document.body.dataset.scheme = theme.scheme;
});
```

Und das CSS, das der Darstellung folgt:

```css
body {
  margin: 0;
  background: var(--pithyx-color-background);
  color: var(--pithyx-color-text);
  font: var(--pithyx-font-size) var(--pithyx-font-ui);
}
button {
  height: var(--pithyx-control-height);
  padding: 0 calc(var(--pithyx-space) * 2);
  border: 1px solid var(--pithyx-color-border);
  border-radius: var(--pithyx-radius);
  background: var(--pithyx-color-accent);
  color: var(--pithyx-color-accent-text);
}
button:focus-visible {
  outline: 2px solid var(--pithyx-color-focus);
}
```

## Sprache und Formate

- `context.language` ist die Sprache des Benutzers für Texte, zum Beispiel `de`. `context.locale` ist diese Sprache mit der Region der Formate der Box, zum Beispiel `de-AT`; das SDK setzt sie als `lang` auf das Wurzelelement. `context.timeZone` ist eine IANA-Zone wie `Europe/Berlin`.
- `createI18n(app, { en, de, ... })` braucht `en`. Es nimmt die Texte der genauen Locale, dann ihrer Grundsprache (`de` für `de-AT`), dann `en`, und liest `context.locale` bei jedem Aufruf; so folgt es `localeChange` von selbst. Ein Schlüssel, der einer Sprache fehlt, fällt auf den englischen Text zurück; ein Schlüssel, den keine Sprache hat, kommt als der Schlüssel selbst zurück. Schlüssel sind Pfade mit Punkten in verschachtelte Objekte.
- Texte sind ICU MessageFormat: `{count, plural, one {# Datei} other {# Dateien}}`, `{name}`, `{kind, select, ...}`. Jeder Text wird einmal pro Locale kompiliert.
- `app.format` hat `date`, `time`, `dateTime`, `relative`, `number`, `bytes` (dezimale Einheiten, wie der Desktop Größen zeigt) und `list`. Sie nutzen die aktuelle Locale und Zeitzone. Daten kommen über die API als UTC-Text in ISO 8601; gib sie so weiter, wie sie sind.
- Zeichne bei `localeChange` neu. Wenn dein Handler läuft, hat das SDK den Kontext und `lang` schon aktualisiert.
- Jede andere i18n-Bibliothek geht auch; das SDK gibt nur die Locale weiter.

## Darstellung

Das SDK setzt diese Eigenschaften auf `document.documentElement` (oder auf `themeRoot`, das du `connect` mitgibst, `null` zum Auslassen):

- Farben: `--pithyx-color-background`, `-surface`, `-surface-raised`, `-hover`, `-selected`, `-text`, `-text-muted`, `-border`, `-accent`, `-accent-text`, `-focus`, `-success`, `-warning`, `-danger`,
- Schrift und Form: `--pithyx-font-ui`, `--pithyx-font-mono`, `--pithyx-font-size`, `--pithyx-radius`, `--pithyx-space`, `--pithyx-control-height`.

Außerdem setzt es `color-scheme`, `data-pithyx-theme` (`light` oder `dark`) und `data-pithyx-density` (`comfortable` oder `compact`; compact verkleinert `--pithyx-space` und `--pithyx-control-height` schon). Bei `themeChange` ändern sich die Eigenschaften, bevor dein Handler läuft; CSS folgt ohne Code. Nur Zeichnungen in einem Canvas oder mit WebGL brauchen die Werte aus `theme.tokens`.

Fertige Komponenten im Aussehen des Desktops findest du in [@pithyx/ui](/de/docs/sdk/ui/).

## Sonderfälle

- **Schriften.** Die Schrift-Eigenschaften nennen die Schriften des Desktops, aber die Seite der App teilt die Schriften nicht, die der Desktop geladen hat, und darf Schriften nur aus eigenen Dateien oder `data:`-URLs laden (`font-src 'self' data:`). Liefere die Schriftdateien mit deinem Frontend aus, wenn du dich auf sie verlässt; sonst weicht der Browser auf eine andere Schrift aus.
- **Unbekannte Werte.** Ein Token-Wert mit `;`, `{`, `}`, `<` oder `>` wird nie gesetzt, damit vom Host kein CSS eingeschleust werden kann.
- **Mock-Desktop.** `pithyx dev` schaltet Darstellung, Akzent, Locale (`en-US`, `en-GB`, `de-DE`, `de-AT`, `fr-FR`, `ja-JP`), Zeitzone und Größenklasse im Panel um. Seine Dichte ist immer `comfortable`.

## Grenzen

Die Namen der `--pithyx-*`-Eigenschaften sind ein stabiler Vertrag: Neue Namen sind eine kleine Änderung, einen umzubenennen oder zu entfernen eine große. Rohe Farben gehören nie dazu; nutze die Namen nach ihrem Zweck.
