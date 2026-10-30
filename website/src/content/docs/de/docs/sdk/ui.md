---
title: '@pithyx/ui'
description: 'Die React-Komponenten und Tokens des Desktops, damit Apps von Dritten nativ aussehen.'
status: ready
audience: developer
sidebar:
  order: 3
---

`@pithyx/ui` ist die React-Komponentenbibliothek des Pithyx-Desktops, gebaut auf React Aria Components (Unterstützung für Tastatur, Touch und Screenreader), zusammen mit den Design-Tokens in `tokens.css`. Die Tokens und die Klassennamen für einfache HTML-Elemente funktionieren ohne React. Apps von Dritten können es nutzen, um wie der Desktop auszusehen; das SDK setzt es nicht voraus.

## Installieren

```sh
npm install @pithyx/ui react react-dom
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

`@pithyx/ui/tokens.css` ist die einzige Token-Datei von Pithyx und braucht kein JavaScript. Die Farben folgen der hellen oder dunklen Einstellung des Systems (`light-dark()`); erzwinge eine mit `<html data-theme="light">` oder `"dark"`. In einer App setzt das SDK `data-theme` beim Verbinden und bei jedem Wechsel des Themes auf das Schema des Desktops, sodass die Komponenten dem Desktop folgen, nicht dem Browser. Wähle eine Akzentfarbe mit `<html data-accent="...">`: `petrol` (Standard), `ocean`, `indigo`, `plum`, `moss` oder `graphite`. Jede Voreinstellung ist in beiden Themes auf Kontrast geprüft. Die Token-Namen stehen unter [Themes und Design-Tokens](/de/docs/developers/desktop/theming/).

Die Komponenten lesen die Tokens `--color-*`, `--space-*`, `--radius-*` und `--control-height`, nicht die `--pithyx-*`-Eigenschaften des SDK. Eine App, die die Komponenten nutzt, importiert deshalb `tokens.css`. Sobald das SDK verbunden ist, folgen die Komponenten dem Desktop ohne Code: Das SDK setzt das Farbschema und `data-pithyx-theme` auf dem Wurzelelement, und `tokens.css` übernimmt dann Farben und Schriften des Desktops aus den `--pithyx-*`-Eigenschaften, auch den Akzent des Benutzers; Hover- und Pressed-Töne des Akzents werden daraus abgeleitet.

Für dein eigenes CSS neben den Komponenten nimmst du dieselben Tokens aus `tokens.css` (`--color-*`, `--space-*`, `--radius-*`, `--font-*`, `--control-height`); ihre Namen gehören zur API von `@pithyx/ui` und folgen deren SemVer, eine Umbenennung ist also eine Major-Version. Eine App ohne `@pithyx/ui` nimmt stattdessen die `--pithyx-*`-Eigenschaften des SDK, einen stabilen Vertrag des SDK.

`@pithyx/ui/native` exportiert Klassennamen für native Elemente, die wie die Komponenten aussehen, ohne React Aria: `buttonStyles`, `controlStyles` und `textFieldStyles`, dazu `cx` und `uiTexts`. Es ist für Code gedacht, der klein bleiben muss.

## Komponenten

- Buttons: `Button` (mit `ButtonVariant`) und `IconButton`.
- Felder: `TextField`, `PasswordField`, `SearchField` und `OtpField`.
- Steuerelemente: `Checkbox`, `Switch`, `ProgressBar`, `Badge`, `RadioGroup` mit `Radio` und `Select`.
- Überlagerungen: `Dialog`, `Menu`, `MenuItem`, `MenuTrigger`, `MenuPopover`, `MenuSeparator`, `ContextMenuPopover`, `Tooltip` und `TooltipTrigger`.
- Helfer: `UiProvider`, `useDeliberatePress`, `cx` und `uiTexts`.

Nimm `RadioGroup` für zwei bis vier Möglichkeiten und `Select` (mit `options` aus `id` und `label`) für mehr.

`Checkbox` und `Switch` bekommen ihre Beschriftung als Kinder (`<Checkbox isSelected={done} onChange={setDone}>Erledigt</Checkbox>`), `ProgressBar` als `label`.

Die [API-Referenz](/de/docs/reference/sdk/) hat eine Seite pro Komponente, erzeugt aus ihrem TSDoc (zum Beispiel [Button](/de/docs/reference/sdk/ui/button/) und [Dialog](/de/docs/reference/sdk/ui/dialog/)), und jedes Token mit seinem Wert unter [Tokens](/de/docs/reference/sdk/ui/tokens/).

## Muster

Die Muster bauen ganze Ansichten nach den [Gestaltungsregeln](/de/docs/developers/apps/design/). Sie richten sich nach der Breite ihres Containers, nicht des Bildschirms, und funktionieren deshalb auch in einem schmalen App-Fenster.

- `AppFrame`: der Rahmen einer App mit mehr als einer Ansicht. Links eine Spalte `navigation`, dann der Seitentitel `title`, eine optionale `toolbar`, der Inhalt, ein optionaler `footer` (fest unter dem scrollenden Inhalt) und eine `statusBar`. Unter 600 px wird die Navigation zu einer Listenseite: Ein Eintrag öffnet die Seite, ein Button **Zurück** führt zurück. `scroll="content"` hält Titel und Werkzeugleiste fest, für eine Tabelle, die selbst scrollt.
- `NavList`: die Navigationsspalte. `groups` aus Einträgen mit `icon`, `label` und optionalem `count`, ein Suchfeld, das sie filtert (`searchable`), der aktuelle Eintrag aus `selectedId` mit Akzentbalken, angesagt als aktuelle Seite.
- `Toolbar`, `ToolbarButton` und `ToolbarSeparator`: die Hauptaktion zuerst, die anderen Aktionen in Gruppen, Ansichtsoptionen und Suche in `end`. Die Pfeiltasten wechseln zwischen den Elementen. `variant="bar"` zeichnet einen Streifen über die ganze Breite. Ein `ToolbarButton` hat ein `icon` und ein `label`; ist der Größen-Container der App schmaler als 760 px, zeigt er nur das Symbol und die Beschriftung als Tooltip, damit eine Toolbar mit vielen Aktionen in einer Zeile bleibt.
- `Tabs`, `TabList`, `Tab` (mit optionalem `count`) und `TabPanel`: Tabs unter dem Seitentitel, jedes Panel mit eigener Werkzeugleiste.
- `DataTable`: `columns` mit einer Funktion `cell`, einem `sortValue`, der die Spalte sortierbar macht, `align="end"` für Zahlen und `muted` für Nebenspalten. Einfach- oder Mehrfachauswahl (ein Klick wählt, Strg oder Umschalt erweitern), `onRowAction` für Doppelklick oder Enter, ein `emptyState`, `density="comfortable"` für zweizeilige Zeilen, ein Detailbereich `details` rechts und `virtualized` für lange Listen in einem Container mit begrenzter Höhe.
- `FormSection` und `FormRow`: ein Einstellungsabschnitt mit Titelzeile und Zeilen aus Beschriftung (mit optionalem Hinweis) und Steuerelement. Ein `TextField`, `Select` oder `RadioGroup` in einer `FormRow` übernimmt die Beschriftung der Zeile als Namen; lass sein eigenes `label` weg.
- `ActionBar`: **Verwerfen** und **Übernehmen** mit einer Zeile dazu, was das Übernehmen bewirkt, für den `footer` eines `AppFrame`. Nur für Seiten, deren Änderungen übernommen werden müssen.
- `Card` und `Meter`: ein Objekt mit Zustand: `icon`, `title`, ein `badge`, eine Zeile `facts`, ein optionales `meter` und hinter einem Pfeil die Details (Kinder) und `actions`. `Meter` zeigt eine Menge mit Texten über beiden Enden; gib ihm ein `valueLabel` für Screenreader.
- `StatusBadge`: ein Zustand in Worten mit dem Ton `success`, `warning`, `danger`, `info` oder `neutral`.
- `AttentionList`: **Braucht Aufmerksamkeit** auf einer Übersicht, eine Zeile pro Problem mit der einen Aktion, die es behebt. Ohne Problem zeigt sie nichts.
- `SummaryTiles` und `SummaryTile`: höchstens vier Kennzahlen oben auf einer Übersicht.

```tsx
<AppFrame
  navigation={
    <NavList aria-label="Einstellungen" groups={groups} selectedId={page} onSelect={setPage} />
  }
  title="Netzwerk"
  footer={<ActionBar message="Gilt für jedes Gerät." onApply={apply} onDiscard={reset} />}
>
  <FormSection title="IPv4">
    <FormRow label="IP-Adresse">
      <TextField value={address} onChange={setAddress} />
    </FormRow>
  </FormSection>
</AppFrame>
```

## Icons

`@pithyx/ui` exportiert keine Icons. Der Desktop nutzt Tabler Icons (MIT); damit deine App gleich aussieht, nimm `@tabler/icons-react` mit dem Bereich in deine App auf, von dem `@pithyx/ui` selbst abhängt (`^3.48.0`, damit dein Bundle nur eine Kopie enthält), und importiere jedes Icon, das du nutzt, etwa `import { IconTrash } from '@tabler/icons-react';`. Gib einem `IconButton` sein `label`, das zugleich Tooltip und zugänglicher Name ist.
