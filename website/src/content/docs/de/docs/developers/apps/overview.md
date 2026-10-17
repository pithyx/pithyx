---
title: 'Apps im Überblick'
description: 'Was eine Pithyx-App ist: ein statisches Frontend, ein optionales Backend, ein Manifest und ein Paket.'
status: ready
audience: developer
sidebar:
  order: 1
---

Eine Pithyx-App ist ein statisches Frontend, das der Daemon ausliefert, plus ein optionales Backend aus null oder mehr Containern. Beschrieben wird sie durch ein Manifest (`pithyx.json`), ausgeliefert als eine einzige Paketdatei. Das Frontend läuft in einem Sandbox-iframe auf dem Desktop und spricht mit der Box nur über `@pithyx/sdk`. Die Box baut nie etwas: Sie installiert das fertige Paket.

## Frontend und Backend

- **Frontend.** Statische Dateien unter `/apps/<id>/...`, ausgeliefert nur mit `GET` und `HEAD`. Das Manifestfeld `entry` nennt die HTML-Seite in `frontend/`. Der Desktop öffnet sie in einem Fenster.
- **Backend.** Optional. Eine Teilmenge von Docker Compose, die der Daemon startet und die nur über den Proxy des Daemons unter `/apps/<id>/api/` erreichbar ist (und unter `/apps/<id>/api-<name>/` für weitere Endpunkte). Es werden nie Host-Ports veröffentlicht. Eine Backend-Instanz bedient alle Benutzer; wer gerade aufruft, erfährt sie aus Headern, die der Proxy setzt. Siehe [Backends](/de/docs/developers/apps/backend/).
- **Kein Backend.** Eine App ohne den Schlüssel `backend` läuft nur im Browser. Der Installationsdialog weist darauf hin.
- **Reservierte Pfade.** Ein Frontend-Pfad, dessen erstes Segment `api` ist oder mit `api-` beginnt, gehört dem Backend und liefert für Dateien 404.
- **Icon.** Immer `icon.svg` im Wurzelverzeichnis des Pakets (kein Manifestfeld), ausgeliefert unter `/api/apps/{id}/icon`. Höchstens 64 KB, ein gültiges SVG mit quadratischer viewBox und ohne Skripte, `foreignObject`, Bilder oder iframes.

Pithyx unterstützt keine Web-Apps mit eigenem Port, keine Apps, die mit anderen Apps sprechen, keine Desktop-Widgets und keinen App-Code ohne Fenster. Ein Backend ist der einzige Code, der ohne Fenster läuft.

## Die Sandbox

Eine App von Drittanbietern läuft in `<iframe sandbox="allow-scripts allow-forms">` auf demselben Host wie der Desktop, ohne `allow-same-origin`. Die App hat einen opaken Origin, bekommt keine Cookies und kann `localStorage` oder IndexedDB nicht sinnvoll nutzen. Daten speichert sie über das SDK (`app.storage`), Dateien über `app.files`.

Der Daemon hängt diese Header an jede Antwort unter `/apps/<id>/`, auch an 404 und an Antworten des Backends über den Proxy (Frontend-Dateien bekommen außerdem `Cache-Control`, siehe unten):

```
Content-Security-Policy: sandbox allow-scripts allow-forms; default-src 'self'; script-src 'self';
  style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:;
  media-src 'self' data: blob:; worker-src 'self' blob:; object-src 'none'; base-uri 'none';
  form-action 'self'; frame-ancestors 'self'; connect-src 'self'
X-Content-Type-Options: nosniff
Access-Control-Allow-Origin: *
Referrer-Policy: no-referrer
```

Mit der Berechtigung `network.internet` endet die Richtlinie auf `connect-src 'self' https:`. Daraus folgt:

- Keine Inline-Skripte und keine Skripte von einem CDN. Inline-Styles sind erlaubt.
- Bilder, Schriften und Medien kommen von der App selbst oder aus `data:`- und `blob:`-URLs, auch mit der Internet-Berechtigung.
- Aufrufe an andere Server (`fetch`, WebSocket) nur mit `network.internet`.

`pithyx dev` liefert dieselbe Richtlinie aus, sodass eine blockierte Anfrage schon während der Entwicklung auffällt.

Frontend-Dateien sind so öffentlich wie die eigenen Dateien des Desktops; der Zugriff wird entschieden, wenn das Fenster seine Tokens bekommt. Berechtigungen prüft der Daemon bei jedem Aufruf.

### Kompression und Caching

Beim Installieren schreibt die Box von jeder Textdatei des Frontends ab 1 KB (JavaScript, CSS, HTML, JSON, Source Maps, SVG, Text, XML, WebAssembly) eine Brotli- und eine gzip-Kopie und liefert die Kopie aus, die der Browser annimmt, mit `Content-Encoding` und `Vary: Accept-Encoding`. Sie komprimiert nur, was im Paket steht; gebaut wird auf der Box nie.

Eine Datei unter `assets/`, deren Name auf einen Build-Hash endet, wie Vite ihn schreibt (`assets/index-B4kz_x9Q.js`: ein Bindestrich, acht Zeichen aus Buchstaben, Ziffern, `_` oder `-`, mindestens eines davon kein Kleinbuchstabe), bekommt `Cache-Control: public, max-age=31536000, immutable`; alle anderen Dateien behalten `no-cache`. Der Browser behält gehashte Dateien über Fenster und Neuladen hinweg, prüft `no-cache`-Dateien eines Sandbox-Fensters aber nicht neu, deshalb laden diese mit jedem Fenster neu. Also:

- Behalte unter `assets/` die Standard-Dateinamen von Vite; alles, was die Einstiegsseite lädt, sollte gehasht sein.
- Lege unter `assets/` nie eine Datei ab, deren Name wie ein Hash aussieht, deren Inhalt sich aber zwischen Versionen ändert: Nutzer würden die alte Datei ein Jahr lang behalten.
- Halte die Einstiegsseite klein; sie lädt mit jedem Fenster.

## App-IDs

Eine ID besteht aus der umgekehrten Domain des Autors plus dem App-Namen, drei bis fünf Teile, zum Beispiel `com.mueller.chat` oder `io.github.jan.chat`.

- Jeder Teil nutzt Kleinbuchstaben, Ziffern und Bindestriche; der erste Teil beginnt mit einem Buchstaben. Die ganze ID hat höchstens 100 Zeichen. Muster: `^[a-z][a-z0-9-]*(\.[a-z0-9][a-z0-9-]*){2,4}$`.
- `org.pithyx.*` ist für System-Apps und den offiziellen Katalog reserviert. Die Box lehnt es für ein per Sideload installiertes Paket ab ([`app.manifest.id_reserved`](/de/docs/reference/errors/#app.manifest.id_reserved)).
- Eine ungültige ID ergibt [`app.manifest.id_invalid`](/de/docs/reference/errors/#app.manifest.id_invalid).
- IDs sind auf einer Box eindeutig.

## Das Paket

Ein Paket heißt `<id>-<version>.pithyxapp`. Es ist ein gzip-komprimiertes tar-Archiv und deterministisch: Dieselben Dateien ergeben dieselben Bytes (Manifest zuerst, Icon als Zweites, der Rest sortiert, feste Rechte, fester Besitzer und feste Zeiten). Es enthält:

- `pithyx.json` und `icon.svg`,
- `frontend/` mit dem gebauten Frontend,
- optional `backend/compose.yaml` und `backend/images.lock.json` sowie `backend/images/<arch>/<config digest>.tar` für Offline-Installationen (benannt nach dem Digest der Image-Konfiguration, der im klassischen Image-Store von Docker die Image-ID ist, nicht nach dem Manifest-Digest, den `compose.yaml` festlegt),
- die Dateien README und LICENSE, falls vorhanden,
- `CHECKSUMS` im Format von `sha256sum -c`.

Pfadregeln: relative Pfade mit `/`, ASCII-Buchstaben, Ziffern und `._~@+-`, kein leeres Segment und kein Segment, das mit einem Punkt beginnt, höchstens 512 Zeichen pro Pfad, 255 pro Segment und 32 Segmente. Namen, die sich nur in der Groß- und Kleinschreibung unterscheiden, werden abgelehnt.

Grenzen beim Hochladen eines Pakets auf eine Box: 256 MB für die Datei, 512 MB entpackt, 10.000 Einträge und 64 KB für `pithyx.json`. Nur ein Administrator kann hochladen (`PUT /api/apps/packages`, 15 Minuten aufbewahrt) und dann installieren. Ein aus einer Datei installiertes Paket wird als Sideload markiert und als nicht geprüft angezeigt.

Pakete baust du mit dem Kommandozeilenwerkzeug: siehe [@pithyx/cli](/de/docs/sdk/cli/) und [Deine erste App](/de/docs/developers/apps/first-app/).

## SDK-Leitfäden

Wie ein Frontend mit der Box arbeitet, Aufgabe für Aufgabe:

- [Dateien und App-Speicher](/de/docs/sdk/guides/files-and-storage/)
- [Das Backend aufrufen](/de/docs/sdk/guides/backend/)
- [Ereignisse, Fensterkontext und Startargumente](/de/docs/sdk/guides/events-and-context/)
- [Berechtigungen und permissionsChange](/de/docs/sdk/guides/permissions/)
- [Sprachen und Darstellung in einer App](/de/docs/sdk/guides/i18n-and-theming/)
- [Logging und Fehler](/de/docs/sdk/guides/logging-and-errors/)
- [Testen mit pithyx dev und dem Mock-Desktop](/de/docs/sdk/guides/testing/)
