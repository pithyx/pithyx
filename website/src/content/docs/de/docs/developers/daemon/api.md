---
title: 'API'
description: 'Die REST- und WebSocket-API, Authentifizierung, Topics und der generierte Client.'
status: ready
audience: developer
sidebar:
  order: 2
---

Der Daemon bietet eine REST-API unter `/api` und einen WebSocket unter `/ws`. Beide gehören zur selben Sitzung: Du meldest dich mit einem Cookie an, sendest bei jedem Schreibzugriff einen CSRF-Header und abonnierst Topics für Live-Daten. Das OpenAPI-Dokument wird aus dem Code generiert und ist die Quelle des TypeScript-Clients.

## REST

Das OpenAPI-Dokument ist `packages/api-client/openapi.json` ("Pithyx API", Version `v1`). Ein laufender Daemon liefert es anonym unter `GET /api/openapi.json` aus. Es gibt kein Präfix `/api/v1`. Die Endpunkte sind nach Tags gruppiert: App API, Apps, Auth, Desktop, FileServices, Files, Installer, Jobs, Logs, Notifications, Permissions, Setup, Shares, Storage, Store, System und Users. Siehe die [API-Referenz](/de/docs/reference/api/).

Nicht Teil des OpenAPI-Dokuments: der WebSocket unter `/ws`, WebDAV unter `/webdav/`, die App-Frontends und der Backend-Proxy unter `/apps/<id>/` sowie die statischen Dateien des Desktops.

Konventionen:

- JSON nutzt camelCase, Enums sind Strings, und alle Eigenschaften sind immer vorhanden (nullable als `null`).
- Daten sind ISO 8601 in UTC.
- Jede Antwort hat einen `X-Request-Id`-Header. Fehler sind Problem Details nach RFC 9457; siehe [Fehler, Tracing und Logging](/de/docs/developers/daemon/errors-and-logging/).
- `GET /api/health` ist anonym und liefert `{ "status": "ok", "mode": ... }`.
- Aktualisierungen von Layout und Desktop-Elementen nutzen optimistische Nebenläufigkeit mit `If-Match`.
- Statistik-Topics sind auf 1 Hz gedrosselt.

Apps nutzen eine eigene Oberfläche, `/api/app/**`, die nur Host-Tokens von Apps akzeptiert, nie das Sitzungs-Cookie. Siehe [Apps im Überblick](/de/docs/developers/apps/overview/).

## Authentifizierung und CSRF

1. `POST /api/auth/login` liefert den Benutzer oder eine Zwei-Faktor-Challenge, die 5 Minuten gültig ist.
2. `POST /api/auth/login/totp` schließt die Challenge mit einem Code der Authenticator-App oder einem Wiederherstellungscode ab.
3. Die Antwort setzt das Cookie `pithyx_session` (HttpOnly, `SameSite=Strict`, Pfad `/`). Das Flag `Secure` wird nur gesetzt, wenn die Anfrage selbst HTTPS nutzt, weil reines HTTP per Adresse funktionieren muss.

Der Server speichert nur den SHA-256 des Sitzungs-Tokens. Eine Sitzung läuft nach 7 Tagen ohne Nutzung oder insgesamt nach 30 Tagen ab. Ein offener WebSocket hält sie am Leben und wird stündlich geprüft.

Jede Anfrage unter `/api`, die nicht `GET`, `HEAD` oder `OPTIONS` ist, muss den Header `X-Pithyx-Request: 1` tragen, auch die Anmeldung. Ohne ihn antwortet der Daemon mit [`auth.csrf.header_missing`](/de/docs/reference/errors/#auth.csrf.header_missing). Eine abgelaufene oder fehlende Sitzung ergibt 401 [`auth.session.expired`](/de/docs/reference/errors/#auth.session.expired).

Nur fehlgeschlagene Anmeldeversuche zählen für das Rate-Limit: 5 pro Client-Adresse und Benutzername und 30 pro Client-Adresse in einem festen Fenster von 5 Minuten. Die Antwort ist 429 [`auth.rate.limited`](/de/docs/reference/errors/#auth.rate.limited) mit einem `Retry-After`-Header. Derselbe Limiter schützt die Dateiprotokolle.

Passwörter nutzen Argon2id (64 MiB, 3 Durchgänge) und brauchen mindestens 8 Zeichen. Pithyx hat keine persönlichen Zugriffs-Tokens für Skripte; ein Skript meldet sich an wie der Desktop. Passkeys sind nicht gebaut.

## WebSocket und Topics

`GET /ws` stuft eine authentifizierte Anfrage auf einen WebSocket hoch. Der `Origin` eines Browsers muss zum Host passen, sonst schließt der Daemon mit 4403; Clients ohne `Origin`-Header werden akzeptiert.

Client-Nachrichten sind `sub` und `unsub` mit einer ID und einem Topic. Der Server antwortet mit `ack`, dann `snap` (der vollständige Zustand, einmal) und `event` (Änderungen), oder mit `error` und einem Code. Die größte Client-Nachricht ist 16 KB. Ein Client, der nicht mitkommt (Ausgangspuffer von 256 Nachrichten), wird mit 1008 geschlossen.

| Close-Code | Bedeutung            | Aktion des Clients    |
| ---------- | -------------------- | --------------------- |
| 1000       | Normales Schließen   | keine                 |
| 1001       | Daemon-Neustart      | neu verbinden         |
| 1008       | Client zu langsam    | neu verbinden         |
| 1009       | Nachricht zu groß    | neu verbinden         |
| 4401       | Sitzung beendet      | Anmeldung anzeigen    |
| 4403       | Verboten             | anhalten              |

Topic-Muster dürfen `+` für eine Ebene und `#` für den Rest nutzen. Berechtigungen werden für jeden Snapshot und jedes Ereignis geprüft. Topics heute: `system/info`, `system/resources`, `system/processes`, `system/network`, `system/updates`, `storage/disks`, `storage/pools`, `storage/alerts`, `shares/list`, `jobs`, `jobs/+`, `notifications`, `session/permissions`, `files/+/+`, `thumbnails/+/+`, `apps`, `apps/admin`, `apps/+/context` und `store/admin`.

## Generierter Client

Das Paket `@pithyx/api-client` (privat, nicht veröffentlicht) wird aus dem OpenAPI-Dokument generiert. Beim Bauen des Daemons entsteht `openapi.json`; `pnpm generate` erzeugt dann `src/generated/schema.ts`, `error-codes.ts` und `close-codes.ts` sowie die Manifest-Typen des SDK neu. Bearbeite generierte Dateien nie; bei einem Merge-Konflikt nimmst du eine Seite und generierst neu. `pnpm check:repo` schlägt fehl, wenn sie veraltet sind.

Der Client umhüllt `openapi-fetch`, fügt den CSRF-Header hinzu und wirft `PithyxApiError` (Status, Code, Parameter, Request-ID).

## Stabilität

Die REST- und WebSocket-API, die SDK-API, das Manifest-Schema und die Fehlercodes sind stabile Verträge: Eine inkompatible Änderung braucht eine Major-Version. Pithyx hat noch kein Release, daher kann sich die API noch ändern.
