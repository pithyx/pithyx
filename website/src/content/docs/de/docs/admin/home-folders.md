---
title: 'Home-Ordner'
description: 'Jeder Benutzer bekommt einen privaten Home-Ordner auf dem Home-Pool; Administratoren wählen den Pool, setzen Quotas und entscheiden, was mit dem Home eines gelöschten Benutzers passiert.'
status: ready
audience: admin
sidebar:
  order: 5
---

Jeder Benutzer bekommt einen eigenen Home-Ordner: einen privaten Ort für Dateien, den nur dieser Benutzer und die Administratoren öffnen können. Benutzer sehen ihn als **home** in Dateien und über jeden Dateidienst; Administratoren sehen zusätzlich jedes andere Home unter **homes**. Einstellungen > **Home-Ordner** zeigt, auf welchem Pool die Homes liegen, ihren Platz und ihre Quotas sowie die Homes gelöschter Benutzer.

## Wo Benutzer ihn finden

| Wo             | Das eigene Home                         | Alle Homes (nur Administratoren)                                   |
| -------------- | --------------------------------------- | ------------------------------------------------------------------ |
| Dateien        | **home**, ganz oben in der Seitenleiste | **homes** in der Seitenleiste, das jedes Home als Ordner auflistet |
| SMB            | `\\<box-address>\home`                  | `\\<box-address>\homes`                                            |
| SFTP           | Der Ordner `home`                       | Der Ordner `homes`, ein Ordner pro Benutzer                        |
| WebDAV und FTP | Der Ordner `home` ganz oben             | Der Ordner `homes`, ein Ordner pro Benutzer                        |

Ein Home wird wie ein freigegebener Ordner benutzt: Es hat seinen eigenen [Papierkorb](/de/docs/user/files/#der-papierkorb), Dateien darin gehören dem Benutzer, und der Benutzer braucht das Recht für ein Protokoll, um es darüber zu erreichen (siehe [Dateidienste](/de/docs/admin/file-services/)). Niemand sonst kann Zugriff auf ein Home bekommen, und es erscheint nie unter Einstellungen > **Freigegebene Ordner**. Apps, die die freigegebenen Ordner des Benutzers lesen dürfen, sehen auch sein eigenes Home, nie das eines anderen.

:::caution
Dateien, die in der SMB-Freigabe `homes` gelöscht werden, landen in keinem Papierkorb; sie sind weg. Jedes Home ist ein eigenes Dataset, und Samba kann keine Datei darüber hinweg verschieben. Über `home`, in Dateien, SFTP, WebDAV und FTP funktioniert der Papierkorb wie gewohnt.
:::

## Der Home-Pool

Jedes Home ist ein Dataset `<pool>/pithyx/homes/<benutzer>`, eingehängt unter `/<pool>/homes/<benutzer>`. Sobald es den ersten Pool mit dem Pithyx-Layout gibt, wird er zum Home-Pool, und jeder Benutzer ohne Home bekommt dort eines; ein Benutzer, der vorher angelegt wurde, bekommt sein Home, sobald der Pool da ist. Später angelegte Benutzer bekommen ihres sofort. Die Details eines Benutzers unter Einstellungen > **Benutzer und Gruppen** zeigen den Pfad seines Homes oder „Noch kein Pool“.

Um alle Homes auf einen anderen Pool umzuziehen, wähle den Pool unter **Umziehen nach** und dann **Home-Ordner umziehen**. Ein Pool wird nur angeboten, solange er online, beschreibbar, entsperrt ist und das Pithyx-Layout hat; außerdem braucht er Platz für alle Homes ([`homes.pool.no_space`](/de/docs/reference/errors/#homes.pool.no_space)). Der Umzug läuft als Job, ein Home nach dem anderen:

1. Das Home wird schreibgeschützt, sodass man es lesen, aber nicht ändern kann, während es kopiert wird.
2. Ein Snapshot davon wird mit Dateien, Eigentümern, Rechten und Papierkorb auf den neuen Pool kopiert.
3. Das Home wird vom neuen Pool genutzt, und die alte Kopie wird mit ihren Snapshots gelöscht.

Kann ein Home nicht umziehen, bleibt es, wo es war, und lässt sich wieder ändern; die vorher umgezogenen Homes bleiben umgezogen, und der Home-Pool bleibt der alte, bis jedes Home angekommen ist. Starte den Umzug erneut, sobald die Ursache behoben ist. Es läuft immer nur ein Umzug ([`homes.move.running`](/de/docs/reference/errors/#homes.move.running)).

Ein Home auf einem Pool, der fehlt (zerstört oder exportiert und nicht wieder importiert), lässt sich nicht umziehen; der Umzug lässt es, wo es ist. Kommt der Pool nicht zurück, **lösche** das Home auf dieser Seite: Der Benutzer bekommt ein neues, leeres Home auf dem Home-Pool.

## Quotas

**Standard-Quota** gilt für jedes Home ohne eigene Quota; lass das Feld leer für keine Grenze. Wähle ein Home und in der Werkzeugleiste über der Tabelle **Quota**, um die eigene des Benutzers zu setzen: **Standard-Quota verwenden**, **Keine Grenze** oder **Eigene Quota**. Eine Quota ist mindestens 1 MiB groß ([`homes.quota.invalid`](/de/docs/reference/errors/#homes.quota.invalid)) und zählt die Dateien und die Snapshots des Homes. Ist ein Home voll, schlägt Schreiben fehl, bis Dateien gelöscht und der Papierkorb geleert ist.

## Wenn ein Benutzer gelöscht wird

Beim Löschen eines Benutzers unter Einstellungen > **Benutzer und Gruppen** fragt Pithyx, was mit dem Home-Ordner passiert:

- **Behalten** (die Vorgabe): Das Home bleibt, umbenannt in `<name>.deleted-<uid>` (die Linux-Benutzer-ID), damit der Name für einen neuen Benutzer frei ist. Administratoren sehen es unter **homes**.
- **Dateien in einen freigegebenen Ordner verschieben**: Ein neuer Ordner mit dem Namen des Benutzers im gewählten freigegebenen Ordner bekommt alle Dateien, danach wird das Home samt Papierkorb gelöscht. Das läuft als Job.
- **Mit allen Dateien löschen**: Das Home wird mit seinen Dateien, seinem Papierkorb und seinen Snapshots gelöscht, nachdem du den Benutzernamen eingegeben hast.

Ein behaltenes Home erscheint auf der Seite **Home-Ordner** mit „Benutzer gelöscht“. Wähle es aus, dann tun **In Freigabe verschieben** und **Löschen** in der Werkzeugleiste später dasselbe wie oben; **Löschen** fragt nach dem Ordnernamen. Ein neuer Benutzer mit demselben Namen bekommt nie ein altes Home: Ein Home, das einer anderen Benutzer-ID gehört, wird vorher umbenannt.

## Grenzen

- Homes haben noch keinen Ordner `Desktop` für die Desktop-Elemente.
- Ein Home wird nie mit anderen Benutzern geteilt; dafür gibt es freigegebene Ordner.
