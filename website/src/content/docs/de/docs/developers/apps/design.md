---
title: 'Gestaltungsrichtlinien'
description: 'Wie eine App aussieht und sich verhält wie der Rest von Pithyx: App-Rahmen, Werkzeugleisten, Tabellen, Formulare, Zustände und Rechte.'
status: draft
audience: developer
sidebar:
  order: 6
---

Die eigenen Apps von Pithyx folgen einem gemeinsamen Satz von Gestaltungsregeln (D394): geordnet und ruhig, mit einer Navigationsspalte, einer Werkzeugleiste, Tabellen und Einstellungsseiten, die in jeder App gleich aussehen. Eine Drittanbieter-App muss ihnen nicht folgen, aber in einer App, die es tut, finden sich die Leute sofort zurecht. Diese Seite beschreibt die Regeln innerhalb eines App-Fensters; alles darum herum zeichnet der Desktop (Titelleiste, Fensterknöpfe, Knopf in der Taskleiste, Kontextmenüs).

## Der App-Rahmen

- Eine App mit mehr als einer Ansicht hat links eine Navigationsspalte (220 bis 270 px): oben ein Suchfeld, wenn es etwas zu suchen gibt, Gruppen unter kleinen Überschriften in Großbuchstaben, Einträge mit Icon und Namen. Der aktive Eintrag hat einen Hintergrund und links einen 3 px breiten Balken in der Akzentfarbe; Zähler stehen als kleine Badges am Ende des Eintrags.
- Der Inhaltsbereich beginnt mit dem Seitentitel, dann optional Tabs, dann die Werkzeugleiste, dann der Inhalt; eine Statusleiste unten ist optional (Anzahl, Auswahl, freier Platz).
- Eine App mit nur einer Ansicht (ein Viewer, ein Editor) hat keine Navigationsspalte und behält die Werkzeugleiste.
- In der kompakten Größenklasse (`app.context.sizeClass` ist `compact`, unter 600 px) wird die Navigation zu einer Listenseite, aus der man Einträge öffnet, mit einem Zurück-Knopf, statt einer Schublade über dem Inhalt. Hör auf `sizeClassChange` und baue das Layout mit Container Queries, denn das Fenster entscheidet, nicht das Gerät.
- [`@pithyx/ui`](/de/docs/sdk/ui/#muster) baut diesen Rahmen: `AppFrame` mit einer `NavList`. Er wechselt nach der Breite seines Containers zur Listenseite und funktioniert deshalb auch in einem schmalen Fenster auf einem breiten Bildschirm.

## Werkzeugleisten

- Die Hauptaktion kommt zuerst, als einziger gefüllter Knopf der Ansicht (`Button` in der primären Variante), danach die übrigen Aktionen als Knöpfe mit Icon und Beschriftung, durch Trenner gruppiert. Ansichtsoptionen und ein Schalter für die Detailspalte kommen ans Ende; die Suche ans Ende oder in die Adresszeile.
- Knöpfe haben eine Beschriftung. Nimm `IconButton` nur, wo die Breite nicht reicht, mit Tooltip und zugänglichem Namen.
- `Toolbar` mit `ToolbarSeparator` und dem Bereich `end`; `Tabs` mit einem `count` pro Tab.

## Listen und Tabellen

- Einträge mit mehr als zwei Eigenschaften zeigst du als Tabelle: eine Kopfzeile mit sortierbaren Überschriften, Zeilen von 28 bis 30 px mit feinem Zeiger und mindestens 44 px bei Touch, Nebenspalten in der gedämpften Textfarbe, Zahlen rechtsbündig mit Ziffern gleicher Breite, Zustände als `StatusBadge`.
- Zeig die Details des ausgewählten Eintrags in einer Spalte rechts (250 bis 300 px), die die Werkzeugleiste ein- und ausschaltet, statt in einem Dialog.
- Eine leere Liste sagt in einem Satz, was fehlt, und bietet die Hauptaktion an.
- `DataTable` erledigt all das, samt Auswahl, Sortierung, `emptyState` und Detailbereich `details`; `virtualized` für lange Listen.

## Formulare und Einstellungen

- Bau eine Einstellungsseite aus Abschnitten: eine umrandete Karte mit Titelzeile, die Beschriftung links (etwa 200 px, darunter eine Hinweiszeile), das Steuerelement rechts (höchstens 360 px). In der kompakten Größenklasse untereinander.
- Wenn Änderungen erst beim Übernehmen wirken, bekommt die Seite unten eine feste Leiste mit **Verwerfen** und **Übernehmen** und einer Zeile dazu, was das Übernehmen bewirkt. Einstellungen, die sofort wirken (Schalter, Aussehen), brauchen keine Leiste. Misch nicht beide Arten auf einer Seite.
- Nimm Optionsfelder für zwei bis vier Möglichkeiten, eine Auswahlliste darüber hinaus und einen `Switch` nur für Ein und Aus, das sofort wirkt.
- `FormSection`, `FormRow`, `ActionBar`, `RadioGroup` und `Select` bauen eine solche Seite.

## Zustand und Aufmerksamkeit

- Dinge mit einem Zustand (ein Konto, ein Gerät, ein Job) sind Karten: Icon, Name, ein Zustands-Badge, eine Zeile mit Fakten, eine Füllanzeige, wo eine Menge passt, und aufgeklappt die Details und Aktionen.
- Nenn einen Zustand in Worten, nie nur über die Farbe: Erfolg grün, Warnung orange, Fehler rot, neutral grau.
- Eine Übersicht beginnt mit wenigen Kennzahlen, dann einer Liste dessen, was Aufmerksamkeit braucht, jede Zeile mit der einen Aktion, die es behebt.
- `Card` mit `Meter`, `StatusBadge`, `AttentionList` und `SummaryTiles` bauen das.

## Rechte

Die App kennt den Benutzer (`app.context.user`, darunter `isAdmin` und `groups`) und ihre eigenen Berechtigungen (`app.context.permissions`, aktualisiert durch `permissionsChange`).

- Zeig nicht, was der Benutzer nie tun kann.
- Wo der aktuelle Ort etwas verbietet (ein Ordner, den der Benutzer nur lesen darf), zeig die Aktionen ausgegraut und sag einmal für den Ort, warum, in einer Hinweisleiste, nicht an jedem Knopf.
- Lass den Benutzer ein fehlendes Recht nicht erst durch einen Fehler nach einem Klick erfahren, wenn die App es vorher wissen kann.

## Aussehen

- Nimm die Komponenten von [`@pithyx/ui`](/de/docs/sdk/ui/) und für alles andere die Tokens aus `tokens.css`: keine rohen Farben, Größen, Radien oder Schatten. Die Radien bleiben klein: `--radius-control` (4 px) für Steuerelemente, `--radius-card` (5 px) für Karten und Abschnitte, `--radius-tag` (3 px) für Schlagwörter in Zeilen.
- Halt die Flächen ruhig: neutrale Hintergründe, den Akzent nur für die Hauptaktion, die Auswahl, den aktiven Navigationseintrag und den Fokus.
- Das SDK setzt das helle oder dunkle Schema des Desktops und den Akzent des Benutzers auf die Wurzel der App; prüf jeden Bildschirm in beiden Schemata.
- Liefer Schriften und Icons im App-Paket mit. Die Schriften des Desktops sind IBM Plex Sans für Text und JetBrains Mono für Pfade, Logs und Code; `tokens.css` nennt sie in `--font-sans` und `--font-mono`.
