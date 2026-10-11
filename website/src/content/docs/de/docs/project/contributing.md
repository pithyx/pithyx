---
title: 'Mitmachen'
description: 'Wie du Fehler meldest, Änderungen vorschlägst und Pull Requests schickst.'
status: draft
audience: developer
sidebar:
  order: 3
---

Pithyx wird offen auf GitHub entwickelt, unter `pithyx/pithyx`. Pull Requests gehen an den Branch `dev`, Commits tragen ein Sign-off, und jeder Pull Request folgt einer kurzen Checkliste. Das erste Release wird 1.0.0-beta.1; es gibt noch kein Release.

## Fehler melden

Eröffne ein Issue im Repository und wähle **Bug report** oder **Feature request**; leere Issues sind abgeschaltet, und die Auswahl verlinkt diese Dokumentation. Das Fehlerformular fragt, was passiert ist, die Schritte, die Pithyx-Version, den Bereich, Hardware und Browser sowie den Fehlercode mit Logzeilen (ohne Geheimnisse). Melde Sicherheitsprobleme nicht öffentlich: siehe [Sicherheitslücke melden](/de/docs/project/security/).

## Pull Requests

1. Richte den Build ein, wie in [Am Daemon entwickeln](/de/docs/developers/daemon/development/) beschrieben. `pnpm install` installiert auch die Git-Hooks.
2. Zweige von `dev` ab, als `feature/<name>`, `fix/<name>` oder `hotfix/<name>`. `master` ist stabil und bekommt nur Release-Pull-Requests und Hotfixes. Pushe nie direkt auf `master` oder `dev`; der Pre-Push-Hook verweigert es.
3. Eröffne den Pull Request gegen `dev`. Sein Titel folgt dem Commit-Format unten. Die Vorlage für Pull Requests fragt nach einer Zusammenfassung, wie getestet wurde, und nach der Checkliste unten.

Checkliste:

- Build, Lint und Tests laufen durch: `dotnet test` in `daemon/`, `pnpm lint`, `pnpm typecheck`, `pnpm test`, `pnpm build`, `pnpm check:repo`.
- Generierte Clients sind aktuell (`pnpm generate`).
- Neuer sichtbarer Text hat Schlüssel in `en` und `de`, und neue Fehlercodes haben Übersetzungen.
- Der Changelog hat eine Zeile unter „Unreleased“.
- Die Dokumentation unter `website/` wurde geprüft und, wo die Änderung sie berührt, auf Englisch und Deutsch aktualisiert.
- Keine Em-Dashes und kein nicht englischer Text außerhalb von Übersetzungsdateien.
- Jeder Commit hat ein Sign-off.

YAML-Dateien im Repository enden auf `.yaml`; der Repo-Check verweigert `.yml`.

Code-Stil: kurze englische Kommentare nur dort, wo die Logik nicht offensichtlich ist, Warnungen sind in C# und TypeScript Fehler, und keine Abstraktion ohne eine zweite echte Implementierung.

## Commit-Regeln

Commits nutzen ein Gitmoji, einen Typ, einen optionalen Scope und einen englischen Betreff im Imperativ:

```
<gitmoji> <type>(<scope>): <subject>
```

Nimm das Unicode-Emoji, keinen `:shortcode:`. Typen: `feat`, `fix`, `docs`, `style`, `refactor`, `perf`, `test`, `build`, `ci`, `chore`, `revert`. Beispiele:

```
✨ feat(desktop): add window snapping
🐛 fix(daemon): handle missing lsblk serial
♻️ refactor(storage): extract zpool parser
```

Jeder Commit muss ein Sign-off tragen, das das [Developer Certificate of Origin](https://developercertificate.org/) bestätigt: Committe mit `git commit -s`. Der Trailer muss zum Autor des Commits passen. Ein Hook prüft die Nachricht, und die CI lehnt Commits ohne Sign-off ab.
