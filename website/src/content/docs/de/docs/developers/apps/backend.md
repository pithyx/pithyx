---
title: 'Backends'
description: 'Container hinter dem Proxy: das Compose-Subset, Endpunkte, Benutzer, Speicher und Netzwerk.'
status: ready
audience: developer
sidebar:
  order: 4
---

Das Backend einer App ist eine Docker-Compose-Datei im Paket, die der Daemon gegen eine Allowlist prüft, umschreibt und ausführt. Browser erreichen es nur über den Proxy des Daemons, jede Anfrage trägt die Identität des angemeldeten Benutzers in Headern, die der Proxy setzt, und die Container laufen in einem privaten Netzwerk, mit eigener Benutzer-ID, auf dem Pool, den der Administrator gewählt hat.

## Compose-Subset

Die Datei ist `backend/compose.yaml`. Die Regeln stehen in `sdk/schema/compose.v1.rules.json` und werden vom CLI und vom Daemon gemeinsam genutzt, sodass `pithyx validate` die Antwort der Box gibt. Ein Schlüssel, der nicht auf der Liste steht, wird abgelehnt ([`app.compose.key_unsupported`](/de/docs/reference/errors/#app.compose.key_unsupported)). Die Box schreibt die Datei um, bevor sie sie ausführt.

**Grenzen:** 65.536 Bytes pro Datei, Verschachtelungstiefe 10, 256 Einträge, 16 Services, 32 Volumes, Speicherlimits von 6 MiB bis 256 GiB, höchstens 256 CPUs. YAML-Anker, Aliase, Tags, Merge-Schlüssel, doppelte Schlüssel und `x-*`-Schlüssel werden abgelehnt.

**Oberste Ebene:** `services` (Pflicht) und `volumes`.

**Pro Service:**

- `image` ist Pflicht und per Digest festgelegt (`name@sha256:...`).
- Erlaubt: `command`, `entrypoint`, `environment`, `working_dir`, `volumes`, `tmpfs`, `depends_on`, `healthcheck`, `expose`, `read_only`, `init`, `stop_signal`, `stop_grace_period`, `shm_size`, `mem_limit`, `cpus`, `cap_drop` und `labels` (nicht mit den Präfixen `com.pithyx` oder `com.docker`).
- Braucht ein erhöhtes Recht, das diese Version nicht vergibt (abgelehnt mit `app.elevated.unsupported`): `network_mode: host`, `pid: host`, `devices`, `cap_add` und `privileged`.
- Mounts sind benannte Volumes oder `tmpfs`. Bind-Mounts werden abgelehnt, weil Host-Pfade nie eingehängt werden. Die Ziele `/proc`, `/sys` und `/dev` sowie die Docker-Sockets werden abgelehnt.
- Mit einer Begründung im Fehlertext abgelehnt: `ports` (Backends sind nur über den Proxy erreichbar), `build` (die Box baut nie), `networks`, `links`, `env_file`, `secrets`, `configs`, `user` (die Box setzt die Benutzer-ID), `restart` (die Box steuert den Lebenszyklus), `logging`, `deploy` (nutze `mem_limit` und `cpus`), `security_opt`, `sysctls`, `ulimits`, `container_name`, `extends`, `profiles` und mehr.

**Quellform.** Während der Entwicklung darfst du `backend/compose.dev.yaml` behalten, die denselben Regeln folgt, dazu `build:` (ein String oder `context`, `dockerfile`, `target`, `args`). Jeder Service hat genau eines von `image` oder `build`. `pithyx build` macht daraus `compose.yaml` und `images.lock.json`. Die Box liest die Dev-Datei nie.

**Image-Sperrdatei.** `backend/images.lock.json` listet für jedes Image (bis zu 16) die Manifest- und Config-Digests für `amd64` und `arm64` auf. Die Box prüft jedes Image, das sie lädt, gegen den Config-Digest ([`app.image.mismatch`](/de/docs/reference/errors/#app.image.mismatch)). Eine fehlende oder veraltete Sperrdatei ergibt [`app.package.lock_invalid`](/de/docs/reference/errors/#app.package.lock_invalid). Images kommen aus `backend/images/<arch>/<digest>.tar` im Paket oder werden per Digest gezogen.

**Was die Box beim Umschreiben der Datei hinzufügt:**

- `user: <uid>:<uid>` (oder `0:0` mit `backend.root`) und die Gruppen-IDs zugeordneter freigegebener Ordner,
- ein privates Netzwerk für die App und ein zweites mit Internetzugang nur, wenn die App `network.internet` hat,
- `restart: "no"` (der Daemon startet und stoppt das Backend), Logging nach journald, `pids_limit: 1024`, `oom_score_adj: 500`, `no-new-privileges`,
- die Labels `com.pithyx.app`, `com.pithyx.service` und `com.pithyx.version`,
- benannte Volumes als Ordner unter `<pool>/.pithyx/apps/<id>/volumes/<name>` und zugeordnete freigegebene Ordner als Bind-Mounts (schreibgeschützt, wenn der Administrator weniger erlaubt hat).

## Endpunkte und Routen

Ein Endpunkt ordnet im Manifest einem Namen einen Service und einen Container-Port zu. Die Route enthält nie den Port:

- Endpunkt `api`: `/apps/<id>/api/...`
- Endpunkt `<name>`: `/apps/<id>/api-<name>/...`

Der Pfad nach dem Präfix wird weitergeleitet. Das SDK bietet `app.backend.fetch(path, { endpoint })`, `url`, `socket` und `token`; siehe [@pithyx/sdk](/de/docs/sdk/sdk/#backend).

**Authentifizierung am Proxy.** HTTP-Anfragen tragen `Authorization: Bearer <token>` oder `?pithyx-token=<token>`. WebSockets tragen ein einmaliges `?pithyx-ticket=<ticket>` für diesen Endpunkt. Ein Cookie authentifiziert nie. Ein Token für eine andere App oder ein ungültiges Token ergibt 401 [`app.token.invalid`](/de/docs/reference/errors/#app.token.invalid).

**Verhalten:**

- Ein Preflight des Browsers (`OPTIONS` mit `Origin: null`) wird mit `Access-Control-Allow-Origin: *` beantwortet; andere Origins bekommen 403.
- Request-Bodys haben am Proxy keine Größenbegrenzung; das Backend entscheidet.
- Das Verbindungs-Timeout beträgt 5 Sekunden. Inaktive WebSockets bleiben bis zu 24 Stunden ohne Aktivität bestehen.
- Ist das Backend gestoppt oder startet es gerade, weckt die Anfrage es und wartet bis zu 30 Sekunden. Läuft es nicht, wird es aktualisiert oder ist es nicht verfügbar, lautet die Antwort 503 [`app.backend.unavailable`](/de/docs/reference/errors/#app.backend.unavailable).
- Der Proxy entfernt aus der Antwort Header, die gefährlich wären (zum Beispiel `Set-Cookie`, `Clear-Site-Data`, HSTS), und fügt die Sandbox-Header hinzu.

## Wer ruft auf

Der Proxy entfernt aus der eingehenden Anfrage `Cookie`, `Authorization`, `Proxy-Authorization`, `Referer`, `Forwarded`, jeden `X-Pithyx-*`- und `X-Forwarded-*`-Header sowie die Query-Parameter `pithyx-token` und `pithyx-ticket`. Danach setzt er:

| Header                               | Wert                                          |
| ------------------------------------ | --------------------------------------------- |
| `X-Pithyx-User`                      | der Benutzername                              |
| `X-Pithyx-User-Id`                   | die Benutzer-ID                               |
| `X-Pithyx-Groups`                    | die Gruppennamen, durch Kommas getrennt       |
| `X-Pithyx-Admin`                     | `true` oder `false`                           |
| `traceparent`                        | der Trace der Anfrage                         |
| `X-Forwarded-Prefix`                 | `/apps/<id>/api` oder `/apps/<id>/api-<name>` |
| `X-Forwarded-For`, `-Host`, `-Proto` | der ursprüngliche Client und Host             |

Ein Backend kann diesen Headern nur vertrauen, weil es ausschließlich über den Proxy erreichbar ist.

**Zeitpläne.** Der Aufruf eines Zeitplans kommt nicht über den Proxy und hat keinen Benutzer: Der Daemon sendet `POST <path>` mit leerem Body und nur `X-Pithyx-Schedule: <ID des Zeitplans>` und `traceparent`, ohne Benutzer-Header. Weil der Proxy jeden eingehenden `X-Pithyx-*`-Header entfernt, kann eine Anfrage aus einem Fenster nie `X-Pithyx-Schedule` tragen; ein Backend kann sich also darauf verlassen: Nimm einen Zeitplan-Pfad nur mit diesem Header an und beantworte ihn ohne mit 403. Ein Lauf erscheint im Log Center (Quelle `daemon`, mit seinem Statuscode), nicht in der Auftragsliste.

**Tokens.** Der Desktop fordert sie mit `POST /api/apps/{appId}/tokens` an (Cookie und CSRF-Header). Ein Token gilt 5 Minuten, hat 256 Zufallsbits und ist an Benutzer, Sitzung, App und Fenster gebunden. `host`-Tokens funktionieren nur für `/api/app/**`, `backend`-Tokens nur für den Proxy. Ein Ticket gilt 30 Sekunden und für eine Verwendung. Jede Verwendung prüft Sitzung, Benutzer und Berechtigung erneut; wird eine Berechtigung entzogen oder meldet sich der Benutzer ab, werden die weitergeleiteten Sockets geschlossen. Tokens und Tickets werden in Logs maskiert.

## Speicher

- Jede App hat ein Dataset auf dem App-Pool, den der Administrator bei der ersten Installation gewählt hat: `/<pool>/.pithyx/apps/<id>/` mit `versions/<version>`, `volumes/` und Laufzeitdateien. Die eigenen Daten von Docker liegen auf demselben Pool. Auf der Systemfestplatte wird nichts gespeichert.
- Deklariere `volumes:` auf oberster Ebene in Compose und hänge sie per Namen ein; sie werden zu Ordnern, die der Benutzer-ID der App gehören.
- Speicheranforderungen aus dem Manifest erscheinen als Compose-Volume mit der `id` der Anforderung. Eine optionale Anforderung, die niemand zugeordnet hat, wird zu einem Ordner im Dataset der App. Eine erforderliche, nicht zugeordnete Anforderung verhindert den Start des Backends.
- Ist der Pool gesperrt oder fehlt er, startet das Backend nicht, und der Store nennt den Grund.
- Ein Update erstellt einen Snapshot des Datasets; ein Rollback stellt ihn wieder her. Bei der Deinstallation wählt der Administrator, ob die Daten behalten werden.
- `app.storage` des SDK (Schlüssel-Wert-Speicher pro Benutzer) ist davon getrennt: Schlüssel bis 128 Zeichen, Werte bis 64 KB, 1 MB pro Benutzer und App.

## Netzwerk

- Jede App bekommt ein eigenes internes Docker-Bridge-Netzwerk ohne Internet. Services erreichen sich über den Service-Namen.
- Mit `network.internet` tritt jeder Service zusätzlich einem zweiten, nicht internen Netzwerk bei.
- Auf dem Host werden keine Ports veröffentlicht, und eine nftables-Regel verwirft neue Verbindungen aus den App-Netzwerken zum Host.
- Jede App hat ihre eigene Benutzer-ID (und Gruppen-ID) von 40000 bis 49999. Mit `root: true` ist der Benutzer im Container root, aber die Remapping-Funktion der User-Namespaces hält ihn auf dem Host ohne Rechte.
- Die Härtung ist immer aktiv: `no-new-privileges`, ein Prozesslimit von 1024, ein erhöhter Out-of-Memory-Score.
- Docker-Socket und `privileged` (die Rechte `docker.socket` und `host.full`) werden in dieser Version abgelehnt. Host-Netzwerk, Host-PID, Geräte und Capabilities brauchen das passende erhöhte Recht, das der Installationsdialog anzeigt.

## Lebenszyklus

- **always:** läuft, sobald es kann.
- **onDemand:** läuft, solange ein Benutzer ein Fenster-Lease hält, eine Anfrage oder ein Zeitplan es hält, und `idleTimeout` Sekunden (Standard 600) nach dem letzten davon. Der Desktop abonniert das Topic `apps/<id>/context` einmal pro App und Tab; das Schließen des letzten Fensters beendet das Lease sofort, und eine verlorene Verbindung hält es noch 2 Minuten.
- Ein fehlgeschlagener Start wird nach 1 Minute wiederholt; das Öffnen eines Fensters wiederholt ihn sofort.
- Start-Timeouts: Compose `up --wait` wartet bis zu 10 Minuten, danach der Health-Check des Manifests bis zu 60 Sekunden.
- Stopp: `docker compose down --timeout 30`. Container bekommen SIGTERM und werden nach 30 Sekunden beendet; Volumes und Images bleiben.
- Zeitpläne wecken das Backend ohne Fenster, rufen `POST path` auf (das Backend bleibt dafür bis zu 10 Minuten gehalten) und lassen es dann leerlaufen.
- Das Fenster sieht den Status `stopped`, `starting`, `running`, `updating` oder `unavailable` (SDK-Ereignis `backendStatus`).
- Docker startet nur, solange eine Backend-App installiert und ihr Pool eingehängt ist.

Pithyx hat kein Keep-alive-Lease für Hintergrundarbeit; nutze `always` oder Zeitpläne.
