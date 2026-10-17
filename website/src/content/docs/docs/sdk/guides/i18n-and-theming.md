---
title: 'Languages and theming in an app'
description: 'Show texts in the user language with @pithyx/sdk/i18n, format dates and sizes with app.format, and follow the desktop theme with CSS properties.'
status: ready
audience: developer
sidebar:
  order: 5
---

An app translates its own texts: the desktop hands it the user's language, formats and time zone in `app.context` and says when they change. `@pithyx/sdk/i18n` is an optional helper for ICU MessageFormat texts, the format the desktop uses itself, and `app.format` formats dates, numbers, sizes and lists. The look comes as CSS custom properties `--pithyx-*` that the SDK sets on the page and keeps current, so plain CSS follows light and dark, the accent color and the density.

## Example

The texts live in one JSON file per language:

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

And the CSS that follows the theme:

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

## Language and formats

- `context.language` is the user's language for texts, for example `de`. `context.locale` is that language with the region of the box's formats, for example `de-AT`; the SDK sets it as `lang` on the root element. `context.timeZone` is an IANA zone such as `Europe/Berlin`.
- `createI18n(app, { en, de, ... })` needs `en`. It picks the messages of the exact locale, then of its base language (`de` for `de-AT`), then `en`, and reads `context.locale` on every call, so it follows `localeChange` by itself. A key a language lacks falls back to the English text; a key no language has comes back as the key itself. Keys are dotted paths into nested objects.
- Messages are ICU MessageFormat: `{count, plural, one {# file} other {# files}}`, `{name}`, `{kind, select, ...}`. Each message is compiled once per locale.
- `app.format` has `date`, `time`, `dateTime`, `relative`, `number`, `bytes` (decimal units, as the desktop shows sizes) and `list`. They use the current locale and time zone. Dates cross the API as UTC ISO 8601 strings; pass them as they are.
- Re-render on `localeChange`. The SDK has already updated the context and `lang` when your handler runs.
- Any other i18n library works too; the SDK only hands over the locale.

## Theme

The SDK sets these properties on `document.documentElement` (or the `themeRoot` you pass to `connect`, `null` to skip):

- colors: `--pithyx-color-background`, `-surface`, `-surface-raised`, `-hover`, `-selected`, `-text`, `-text-muted`, `-border`, `-accent`, `-accent-text`, `-focus`, `-success`, `-warning`, `-danger`,
- type and shape: `--pithyx-font-ui`, `--pithyx-font-mono`, `--pithyx-font-size`, `--pithyx-radius`, `--pithyx-space`, `--pithyx-control-height`.

It also sets `color-scheme`, `data-pithyx-theme` (`light` or `dark`) and `data-pithyx-density` (`comfortable` or `compact`; compact already shrinks `--pithyx-space` and `--pithyx-control-height`). On `themeChange` the properties change before your handler runs; CSS follows without code. Only drawings on a canvas or in WebGL need the values from `theme.tokens`.

For ready components in the desktop's look, see [@pithyx/ui](/docs/sdk/ui/); they follow the same theme by themselves once the SDK connected.

## Edge cases

- **Fonts.** The font properties name the desktop's fonts, but the app's page does not share the fonts the desktop loaded and may load fonts only from its own files or `data:` URLs (`font-src 'self' data:`). Ship the font files with your frontend if you rely on them; otherwise the browser falls back to another font.
- **Unknown values.** A token value with `;`, `{`, `}`, `<` or `>` is never set, so nothing from the host can inject CSS.
- **Mock desktop.** `pithyx dev` switches theme, accent, locale (`en-US`, `en-GB`, `de-DE`, `de-AT`, `fr-FR`, `ja-JP`), time zone and size class from its panel. Its density is always `comfortable`.

## Limits

Names of the `--pithyx-*` properties are a stable contract: new names are a minor change, renaming or removing one is a major change. Raw colors are never part of it; use the names by purpose.
