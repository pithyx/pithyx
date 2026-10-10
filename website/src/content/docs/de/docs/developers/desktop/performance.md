---
title: 'Performance und Budgets'
description: 'Bundle- und Zeitbudgets, das Perf-Gate und wie man darin bleibt.'
status: draft
audience: developer
sidebar:
  order: 4
---

Der Desktop unterliegt zwei Arten von Budgets: Größenlimits (gzip) für den Code, der vor und nach der Anmeldung lädt, und absolute Zeitbudgets, die ein Ende-zu-Ende-Test in einem gedrosselten Browser misst. Die Größenprüfung läuft bei jeder Frontend-Änderung; die Zeitprüfung läuft, wenn ein Pull Request das Label `e2e` trägt.

## Budgets

**Bundle-Größe** (gzip, `pnpm build`, dann `pnpm check:bundle`, Skript `tools/checks/bundle-budget.ts`):

- **Anmelde-Bundle:** alles, was `index.html` vor dem ersten Rendern lädt, plus der größte Chunk `i18n-<lang>`. Es darf 200 KB nicht überschreiten und die aufgezeichnete Baseline (`tools/checks/bundle-budget.json`, 147.995 Bytes) um nicht mehr als 5 Prozent übersteigen.
- **Desktop-Bundle:** was die Anmeldung hinzufügt (der Desktop-Chunk mit seinen statischen Importen und Styles, plus der größte Übersetzungs-Chunk des Desktops). Festes Limit von 150 KB.
- Die Baseline ändert sich nur mit `node tools/checks/bundle-budget.ts --update`.
- Schriften sind eigene woff2-Dateien und gehören nicht zu diesen Zahlen.

**Zeit** (`e2e/tests/budgets.spec.ts`, Playwright gegen den Produktions-Build, den der Daemon ausliefert). Der Browser nutzt das Chrome-Profil "Fast 4G" (9 Mbit/s runter, 1,5 Mbit/s hoch, 165 ms Latenz) mit 4-facher CPU-Drosselung, einem Fenster von 1440x900 und einem kalten Cache in jeder Runde. Jedes Budget ist der Median aus 5 Runden nach einer Aufwärmrunde.

| Messung                                                 | Budget  |
| ------------------------------------------------------- | ------- |
| Kaltstart bis zu einer nutzbaren Anmeldung              | 1800 ms |
| Klick bis zur sichtbaren Rückmeldung                    | 100 ms  |
| Anmeldung bis zu einem nutzbaren Desktop                | 1000 ms |
| Neu laden                                               | 650 ms  |
| Dateien: einen besuchten Ordner öffnen (1000 Dateien)   | 16 ms   |
| Dateien: erste Seite eines neuen Ordners (1000 Dateien) | 100 ms  |

Der Daemon hat einen eigenen Zeittest: die erste Seite eines Ordners mit 100.000 Einträgen in unter 200 ms (Kategorie `TimingBudget`).

Die Zeitbudgets sind absolute Zahlen, keine Toleranz gegenüber früheren Läufen. Nur das Anmelde-Bundle wird zusätzlich mit einer Baseline verglichen.

Faustregeln: keinen Spinner vor 300 ms zeigen, Apps mit dynamischem `import()` öffnen, schmale zustand-Selektoren für sich schnell ändernden Zustand nutzen, sich auf den React Compiler statt auf `useMemo` verlassen und lange Listen virtualisieren.

## Das Perf-Gate

- Der Job `e2e` in `.github/workflows/ci.yml` baut den Daemon und den Desktop und führt `pnpm e2e` (das die Budgets einschließt), `pnpm design:test` und die `TimingBudget`-Tests des Daemons aus.
- Er läuft nur auf Anforderung: mit dem Pull-Request-Label `e2e`, durch manuelles Starten des Workflows oder für jeden Pull Request auf `master`. Füge das Label bei Änderungen an Benutzerabläufen, der Shell, dem Fenstermanager, der Anmeldung, der Einrichtung, dem SDK-Transport, dem API-Vertrag, dem Ladepfad oder dem Bundle hinzu.
- Er startet, nachdem die Daemon-Tests fertig sind, weil parallele Last die Zeiten verfälscht. Bei Pushes auf `dev` werden die Budgets übersprungen (`PITHYX_SKIP_BUDGETS=1`).
- Zeiten schwanken mit der Last der Maschine. Schlägt ein Budget fehl, ohne dass deine Änderung etwas Langsames enthält, prüfe `uptime` und führe es noch einmal aus.
- `pnpm design:test` öffnet jeden Bildschirm der Design-Galerie in 390x844, 820x1180 und 1440x900 in Englisch und Deutsch und schlägt bei Überlauf oder verdeckter Titelleiste fehl. Barrierefreiheit wird mit axe in `e2e/tests/a11y.spec.ts` geprüft, der Token-Kontrast in `packages/ui/src/contrast.test.ts`.

## Messen

```sh
node tools/perf/measure.ts --url http://<box-address>:7484 --user <name> --password '<password>' --runs 5
```

Das Skript braucht ein Chromium, das über die Ende-zu-Ende-Abhängigkeiten installiert wurde, und einen vorhandenen Benutzer. Es führt ein ungedrosseltes Profil und ein Fast-4G-Profil mit 4-facher CPU-Drosselung aus, mit einem kalten Kontext pro Lauf, und gibt den Median von Kaltstart, Klick-Rückmeldung, Anmeldung bis Desktop, erstem Neuladen, warmem Neuladen und der Zeit für den Passwort-Hash aus. Füge die Tabelle in deinen Pull Request ein.

Um das Bundle zu prüfen, führe `pnpm build` und `pnpm check:bundle` aus; es gibt die gzip-Größe jeder Datei und die Summen aus. In der App Dateien zeigt `performance.getEntriesByName('pithyx:files:shown', 'measure')` die Zeit bis zur Anzeige eines Ordners.
