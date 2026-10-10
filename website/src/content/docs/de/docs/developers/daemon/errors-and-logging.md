---
title: 'Fehler, Tracing und Logging'
description: 'Fehlercodes und Problem Details, Request-IDs, strukturierte Logs und das Maskieren von Geheimnissen.'
status: draft
audience: developer
sidebar:
  order: 3
---

Jeder fehlgeschlagene API-Aufruf liefert ein Problem-Dokument nach RFC 9457 mit einem stabilen Fehlercode, und jede Anfrage trägt eine Request-ID, die Antwort, Logs und externe Befehle zusammenhält. Logs sind strukturiertes JSON und bleiben auf der Box; Geheimnisse werden maskiert, bevor sie geschrieben werden.

## Fehlercodes

Ein Code hat die Form `area.object.reason`: Kleinbuchstaben, Punkte zwischen den Teilen, Unterstriche innerhalb von Wörtern, zum Beispiel `fs.file.not_found`. Codes sind Konstanten in `daemon/src/Pithyx.Daemon/Errors/ErrorCodes.cs`; jeder hat ein Attribut `[ErrorStatus]` mit seinem HTTP-Status. Die TypeScript-Union wird daraus generiert. Benenne einen Code nie um und entferne ihn nie ohne eine Major-Version. Die vollständige Liste steht in der [Fehlerreferenz](/de/docs/reference/errors/).

So fügst du einen Code hinzu:

1. Füge die Konstante mit `[ErrorStatus(...)]` in `ErrorCodes.cs` hinzu.
2. Füge den Text in `i18n/en/errors.json` und `i18n/de/errors.json` hinzu.
3. Führe `pnpm generate` aus.

Wirf `new PithyxException(code, params, inner)` in Code, der eine Anfrage hat. Middleware schreibt ein Problem mit `WriteProblemAsync(context, code)`. Ein Status ohne Code fällt auf einen allgemeinen zurück: 400 `request.body.invalid` (mit den Namen der ungültigen Felder in `params.fields`), 404 `request.route.not_found`, 405 `request.method.not_allowed`, 415 `request.media_type.unsupported`, 416 `fs.range.not_satisfiable`, alles andere `system.internal.unexpected`.

## Problem Details

Der Body hat den Content-Type `application/problem+json` und diese Member: `type` (Standard `about:blank`), `title`, `status`, `detail`, `code`, `params` und `requestId`.

- `detail` ist der englische Text des Codes mit eingesetzten Parametern. Er ist ein Fallback für Logs und Skripte.
- Die API liefert nie übersetzten Text. Ein Client übersetzt `code` mit `params`; siehe [Übersetzungen](/de/docs/developers/desktop/i18n/).
- Ein Code, den der Daemon nicht kennt, wird durch `system.internal.unexpected` ersetzt.
- Nur Pfade unter `/api` beantworten nackte Statuscodes mit Problem Details.
- Unerwartete Ausnahmen werden als Fehler geloggt; erwartete (eine `PithyxException`) als Information.

## Request-IDs

Die Request-ID ist die W3C-Trace-ID (32 Hexadezimalzeichen) der `Activity` der Anfrage, keine UUID. Jede HTTP-Anfrage, jede WebSocket-Nachricht, jeder Job und jede Aktualisierung im Hintergrund hat eine.

- Sie wird im Header `X-Request-Id` und in `requestId` eines Problems zurückgegeben.
- Externe Befehle erhalten sie als Umgebungsvariable `TRACEPARENT`. Weitergeleitete App-Backends erhalten sie als Header `traceparent`.
- Der Desktop zeigt sie Benutzern nie; sie geht an die Browser-Konsole und in die Logs.
- Um alles zu einer fehlgeschlagenen Anfrage zu finden, durchsuche die Logs nach ihrer ID.

## Strukturierte Logs

Der Daemon loggt mit Serilog als kompaktes JSON, mit der gerenderten Meldung, an drei Orte:

- die Konsole (journald auf einer Box),
- `daemon.json` im Log-Verzeichnis (`/var/log/pithyx` oder `PITHYX_LOG_DIR`), rotierend bei 20 MiB mit 10 behaltenen Dateien,
- den Speicher des Protokoll-Centers, `logs.db`.

Die Standardstufe ist Information; ASP.NET Core und der Proxy loggen ab Warning. Jeder Eintrag hat die Eigenschaft `RequestId`.

Das Protokoll-Center hält Einträge im Speicher (20.000 Einträge) und schreibt sie im Sekundentakt in Stapeln. Quellen sind `daemon`, `system/<unit>` (aus dem Journal gelesen) und `app/<id>` (App-Logs und Container-Ausgabe). Standardwerte: 30 Tage Aufbewahrung (1 bis 365 Tage), 4 GiB Höchstgröße, eine optionale tägliche Archivkopie auf einen App-Pool. Die Endpunkte sind `GET /api/logs`, `/api/logs/sources`, `/api/logs/export` und, für Administratoren, `GET` und `PUT /api/logs/settings`. Nichts wird irgendwohin gesendet.

Log-Einträge von Apps sind pro App ratenbegrenzt: ein Burst von 200, danach 20 pro Sekunde.

## Geheimnisse

Der Logger ersetzt den Wert jeder Eigenschaft, deren Name auf `password`, `passphrase`, `secret`, `token`, `cookie`, `authorization`, `recovery_key` oder `totp` passt (ohne Beachtung der Groß- und Kleinschreibung, in beliebiger Tiefe), durch `***`. In Textwerten maskiert er `pithyx-token=`, `pithyx-ticket=` und Bearer-Zugangsdaten. Befehlsargumente, die als sensibel aufgeführt sind, werden maskiert, und die Standardeingabe wird nie geloggt. Passwörter werden als Argon2id-Hashes gespeichert und Sitzungen als SHA-256.

Pithyx hat noch keine Support-Bundles.
