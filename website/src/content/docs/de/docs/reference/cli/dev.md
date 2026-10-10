---
title: 'pithyx dev'
description: 'pithyx dev startet den Mock-Desktop mit der App in der Sandbox der Box und einem Proxy zu ihrem Backend, für die Entwicklung ohne Box.'
status: ready
audience: developer
sidebar:
  order: 5
---

`pithyx dev` startet einen Mock-Desktop auf deinem Rechner: dein gebautes Frontend in derselben Sandbox wie auf der Box, die Host-Seite des SDK und einen Proxy zum Backend, der sich wie der der Box verhält. Das Backend läuft mit `docker compose`. Auf der Box gibt es keinen Entwicklungsmodus; mit Vite nutzt du stattdessen das Plugin `@pithyx/cli/vite` und startest `vite` ([@pithyx/cli](/de/docs/sdk/cli/)).

## Aufruf

<!-- generated:cli-synopsis dev -->

```sh
pithyx dev [project folder] [--frontend <folder>] [--port <port>] [--host <host>] [--endpoint <n=url>]... [--no-docker]
```

<!-- /generated -->

## Optionen

<!-- generated:cli-options dev -->

| Option        | Wert       | Typ                  | Standard    | Beschreibung                                                                                                         |
| ------------- | ---------- | -------------------- | ----------- | -------------------------------------------------------------------------------------------------------------------- |
| `--frontend`  | `<folder>` | string               | `dist`      | The built frontend                                                                                                   |
| `--port`      | `<port>`   | string               | `7400`      | The port                                                                                                             |
| `--host`      | `<host>`   | string               | `127.0.0.1` | The address to listen on                                                                                             |
| `--endpoint`  | `<n=url>`  | string, wiederholbar | –           | Reach the endpoint n at url instead of through Docker, for example --endpoint api=http://localhost:8080 (repeatable) |
| `--no-docker` | –          | boolean              | aus         | Do not start the backend; endpoints without --endpoint answer 503                                                    |

<!-- /generated -->

`--port` muss eine ganze Zahl von 1 bis 65535 sein. Ein IPv6-`--host` wird in eckigen Klammern ausgegeben. `--endpoint` nennt einen Endpunkt, den das Manifest deklariert, und eine URL, zum Beispiel `api=http://localhost:8080`; ein nicht deklarierter Name scheitert.

## Was läuft

- Die Seite liegt unter `http://<host>:<port>/`, das Frontend als `/apps/<id>/` mit den Headern der Box. Dateien werden bei jeder Anfrage gelesen: Baue neu und lade neu, um eine Änderung zu sehen.
- Das Backend startet, wenn das Manifest eines hat, ein Endpunkt kein `--endpoint` hat und `--no-docker` fehlt: `docker compose --project-name pithyx-dev-<id mit _ statt Punkten> --file <compose> --file <override> up --detach --wait --build`. `<compose>` ist `backend/compose.dev.yaml`, wenn sie existiert, sonst die Datei aus `backend.compose`. Der Override veröffentlicht den Port jedes Endpunkts auf `127.0.0.1` unter einem Port, den Docker wählt; der Proxy erreicht ihn dort.
- Mit `--no-docker` antworten Endpunkte ohne `--endpoint` mit 503.
- Strg+C oder SIGTERM stoppt den Server, führt `docker compose down` aus (Volumes bleiben) und endet mit 0.

## Dateien

| Datei                                                           | Zugriff                                      |
| --------------------------------------------------------------- | -------------------------------------------- |
| `pithyx.json`                                                   | gelesen; muss existieren und gültig sein     |
| `icon.svg`, der Frontend-Ordner                                 | bei jeder Anfrage gelesen                    |
| `backend/compose.dev.yaml` oder die Datei aus `backend.compose` | von `docker compose` gelesen                 |
| ein temporärer Ordner `pithyx-dev-*` mit `compose.ports.yaml`   | beim Start geschrieben, nach `down` entfernt |

## Exit-Codes

| Code | Wann                                                                                                                                                                              |
| ---- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 0    | Mit Strg+C oder SIGTERM gestoppt.                                                                                                                                                 |
| 1    | `pithyx.json` fehlt oder ist ungültig, `--endpoint` ist falsch geschrieben oder nennt einen nicht deklarierten Endpunkt, Docker scheitert, oder der Port lässt sich nicht öffnen. |
| 2    | `--port` ist kein Port, oder mehr als ein Projektordner.                                                                                                                          |

## Befunde

`dev` gibt keine Befunde aus. Starte [`pithyx validate`](/de/docs/reference/cli/validate/), um zu sehen, warum ein Manifest ungültig ist.

## Beispiele

```sh
pithyx dev                                         # http://127.0.0.1:7400/
pithyx dev --port 8000 --host 0.0.0.0              # von anderen Rechnern erreichbar
pithyx dev --endpoint api=http://localhost:8080    # dein eigener Server für den Endpunkt api
pithyx dev --no-docker                             # nur das Frontend
```
