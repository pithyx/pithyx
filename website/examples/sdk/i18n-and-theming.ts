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
