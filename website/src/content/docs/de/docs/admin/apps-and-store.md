---
title: 'Apps und der Store'
description: 'Apps aus Katalogen oder einer Datei installieren, den App-Pool wählen, Update-Regeln festlegen und wie Backends in Docker laufen.'
status: ready
audience: admin
sidebar:
  order: 10
---

Nur Administratoren installieren Apps, in der App Store. Eine App ist ein Frontend, das der Daemon ausliefert, plus optional ein Backend aus Containern. Apps kommen aus Katalogen (Git-Repositorys mit einem signierten Index) oder aus einer Paketdatei (`.pithyxapp`). Alle App-Daten liegen auf einem Pool, den du bei der ersten Installation wählst, nie auf der Systemplatte.

## Der Store

Der Store hat den Tab **Installiert**, **Updates**, solange Updates warten (mit ihrer Zahl), **Offiziell** und **Community**, solange dieser Katalog an ist, und **Eigene**, sobald du einen eigenen Katalog hinzugefügt hast, und ein Zahnrad für die **Store-Einstellungen**. Offizielle Apps werden vom Pithyx-Projekt geprüft, gebaut und signiert; Community-Apps stammen von Entwicklern außerhalb des Projekts und werden vom Projekt geprüft, bevor sie veröffentlicht werden; **Eigene** zeigt Apps von Katalogen, die du hinzugefügt hast und die das Projekt nicht prüft. Die Abzeichen lauten „Pithyx“, „Community“ und „Nicht geprüft“ (auch für Apps, die aus einer Datei installiert wurden). Die Suche umfasst Name, Beschreibung und Entwickler. Eine installierte App hat eine einzige Seite, egal aus welchem Tab oder welcher Suche du sie öffnest: ihre Aktionen, Berechtigungen, Ordner und Einstellungen, bei einer App aus einem Katalog auch ihre Screenshots, den Link zu ihrem Quellcode und ihre Versionen. Benutzer öffnen installierte Apps über das Pithyx-Menü; siehe [Apps](/de/docs/user/apps/).

## Aus einer Datei installieren

Wähle **Aus Datei installieren** im Tab **Installiert** (oder **Aus Datei aktualisieren** in den Details einer App) und wähle ein Paket. Der Dialog listet die Berechtigungen auf, die die App anfordert, die Ordner, die sie braucht, ob sie ein Backend mitbringt, eine Warnung, wenn ein Container als root läuft, besondere Rechte, **Wer sie nutzen darf** und **Pool für App-Daten**. Bei einem Paket aus einer Datei warnt er: „Dieses Paket stammt aus einer Datei und ist nicht geprüft.“

Grenzen eines Uploads: die Datei höchstens 256 MB, entpackt höchstens 512 MB und 10 000 Einträge, das Manifest höchstens 64 KB ([`app.package.too_large`](/de/docs/reference/errors/#app.package.too_large)). Ein geprüftes Paket wartet 15 Minuten auf deine Bestätigung. Die IDs `org.pithyx.*` werden aus Dateien abgelehnt. Eine Version kann installiert, aktualisiert, neu installiert (ersetzt ihre Dateien) oder herabgestuft werden; ein Herabstufen kann abgelehnt werden ([`app.package.downgrade_unsupported`](/de/docs/reference/errors/#app.package.downgrade_unsupported)), nutze dann **Zurück zur vorigen Version**.

**Deinstallieren** verlangt von dir, **Daten behalten** (eine erneute Installation findet sie wieder, mit der Benutzer-ID der App; der Installationsdialog sagt das dann) oder **Daten löschen** (Datenbanken, Einstellungen, Dateien und Snapshots; nicht umkehrbar) zu wählen. Freigegebene Ordner, die in die App eingebunden sind, bleiben in beiden Fällen. Das Deaktivieren einer App („Nutzer können die App öffnen“ aus) behält ihre Daten und ihren Zugriff. Eine App fragt nach Speicher; du ordnest jeden angeforderten Ordner einem Ordner in einem freigegebenen Ordner zu, schreibgeschützt oder mit Lesen und Schreiben: Wähle den freigegebenen Ordner, öffne den gewünschten Ordner (oder lege ihn mit **Neuer Ordner** dort an) und speichere; der freigegebene Ordner selbst geht auch. Erforderliche müssen zugeordnet sein, sonst startet das Backend nicht. Der Installationsdialog bietet die Zuordnung als letzten Schritt an und zeigt **App öffnen** erst, wenn jeder erforderliche Ordner einen hat; bis dahin sagt das Fenster der App, dass sie auf einen Ordner wartet. Der Schalter „Debug-Meldungen an das Protokoll-Center senden“ schaltet das Debug-Logging für eine App ein.

## Der App-Pool

Bei der ersten Installation wählst du **Pool für App-Daten**: „Alle Apps speichern ihre Daten auf diesem Pool. Er lässt sich nicht ändern, solange eine App installiert ist.“ Ohne Pool sagt der Store, dass du zuerst in Speicher einen anlegen sollst. Der Pool muss importiert, online (oder degradiert), beschreibbar, entsperrt und von Pithyx eingerichtet sein.

Jede App bekommt ein Dataset `<pool>/pithyx/apps/<id>`, eingehängt unter `/<pool>/.pithyx/apps/<id>`. Das Datenverzeichnis von Docker ist `/<pool>/.pithyx/docker`. Fehlt der Pool oder ist er gesperrt, zeigen Apps darauf „Pool nicht verfügbar“, und niemand kann sie öffnen, bis er zurück ist. App-Container laufen mit Benutzer-IDs von 40000 bis 49999.

## Updates und Rollback

![Die Store-Einstellungen mit der Update-Regel Administratoren benachrichtigen, der täglichen Prüfzeit 03:00, dem Kanal Stabil und der Liste der Kataloge](../../../../../assets/screenshots/store-update-settings.de.webp)

_Die Store-Einstellungen: was mit neuen Versionen passiert, wann die Box prüft und welchem Kanal sie folgt._

Öffne das Zahnrad, dann **Updates**. Die Regel „Wenn ein Katalog eine neuere Version hat“ ist **Installieren**, **Administratoren benachrichtigen** (Standard) oder **Nichts tun** (das Update wird nur im Tab **Updates** aufgeführt). Ein Update, das neue Berechtigungen oder besondere Rechte verlangt, installiert sich nie von selbst. Die Zeit der täglichen Prüfung ist standardmäßig 03:00 (Zeitzone der Box) plus eine feste zufällige Verzögerung von 0 bis 3600 Sekunden pro Box; **Jetzt prüfen** führt sie sofort aus. Eine App kann in ihren Details einer eigenen Regel folgen. Administratoren bekommen die Benachrichtigungen „App-Updates“ und „Katalog nicht abgerufen“.

Vor einem Update legt Pithyx einen Snapshot des Datasets der App an. Startet die neue Version nicht, kommt die alte zurück. **Zurück zu** und die Versionsnummer (zum Beispiel **Zurück zu 1.2.0**) kehrt zur vorigen Version zurück, mit den Daten der App, wie sie vor dem Update waren; seitdem gemachte Änderungen und Snapshots gehen verloren. Es wird nur die eine vorige Version aufbewahrt.

Der Kanal ist **Stabil** (Versionen, die die Eigentümer der Kataloge freigegeben haben, Standard) oder **Beta** (jede neue Version, sobald ein Katalog sie veröffentlicht, vor ihrer Freigabe).

## Kataloge und Vertrauen

Der offizielle und der Community-Katalog sind standardmäßig beide an; der Tab **Community** sagt, dass seine Apps von Entwicklern außerhalb des Projekts stammen, vom Projekt geprüft, aber nicht wie eigene verbürgt. Schalte den Community-Katalog in den Store-Einstellungen aus, wenn du nur offizielle Apps willst; das erneute Einschalten fragt „Community-Katalog nutzen?“. Keiner von beiden lässt sich entfernen ([`store.catalog.built_in`](/de/docs/reference/errors/#store.catalog.built_in)). Um einen eigenen hinzuzufügen, gib eine **URL des Repositorys** ein: `https://...` für einen öffentlichen Katalog oder `git@host:owner/repository.git` für einen privaten (keine Zugangsdaten in der URL, höchstens 500 Zeichen; [`store.catalog.url_invalid`](/de/docs/reference/errors/#store.catalog.url_invalid)).

Für einen privaten Katalog über SSH erzeugt die Box einen ed25519-Deploy-Key und zeigt seine öffentliche Zeile; füge sie dem Repository als schreibgeschützten Deploy-Key hinzu und wähle **Jetzt abrufen**. Der Host-Schlüssel des Servers wird bei der ersten Verbindung festgehalten. Ein Abruf bricht nach 3 Minuten ab. Der Abrufstatus ist Aus, Noch nicht abgerufen, Aktuell, Abruf fehlgeschlagen oder Wird abgerufen; ein fehlgeschlagener Abruf behält die letzte geprüfte Kopie.

Die Indizes des offiziellen und des Community-Katalogs tragen eine Ed25519-Signatur, die gegen im Daemon eingebaute Schlüssel geprüft wird, und der Hash jeder Datei wird geprüft. Eigene Kataloge sind unsigniert: „Apps aus deinen eigenen Katalogen prüft das Pithyx-Projekt nicht. Füge nur Kataloge hinzu, denen du vertraust.“ Die IDs `org.pithyx.*` kommen nur aus dem offiziellen Katalog.

## Sonderrechte

Manche Apps brauchen mehr, als die Sandbox einem Backend gibt: das Netzwerk der Box (`host.network`, etwa um Geräte per Broadcast zu finden), die Prozesse der Box (`host.pid`, für die Überwachung), einen Geräteknoten wie eine Grafikkarte zum Umwandeln von Videos oder einen Funkstick für die Hausautomation (`device:/dev/...`) oder eine Linux-Capability für root im Container der App (`capability:<NAME>`, etwa `NET_ADMIN` für ein VPN). Der Installationsdialog und die Detailseite der App zeigen sie unter **Sonderrechte**, jedes mit dem, was es erlaubt, und warum das ein Risiko ist.

- Zum Geben brauchst du eine Bestätigung: dein Passwort noch einmal und, bei aktiver Zwei-Faktor-Anmeldung, einen Code aus deiner Authenticator-App. Die Bestätigung gilt fünf Minuten in der Sitzung, in der du sie gemacht hast; mehrere Rechte hintereinander brauchen sie also nur einmal. Ein falsches Passwort zählt zu den Grenzen für Anmeldeversuche.
- Im Installationsdialog ist **Diese Rechte geben** vorab eingeschaltet. Schalte es aus, um die App ohne sie zu installieren; ihr Backend läuft dann ohne diese Rechte, bis du sie auf der Detailseite der App gibst.
- Jedes Recht hat auf der Detailseite einen Schalter. Zum Entziehen brauchst du kein Passwort; das Backend startet ohne das Recht neu (ein Dienst ohne `host.network` kommt ins eigene Netzwerk der App, ein Gerät oder eine Capability fällt weg). Gibst du es wieder, fragt Pithyx nach der Bestätigung.
- Ein Update, das ein Recht verlangt, das die App noch nicht hatte, braucht die Bestätigung ebenfalls und installiert sich nie von selbst. Ein Recht, das du entzogen hast, bleibt über Updates hinweg entzogen.
- Eine App mit einem Sonderrecht trägt im Store das Abzeichen **Sonderrechte**.
- Jede Bestätigung, auch eine fehlgeschlagene, und jedes gegebene oder entzogene Recht landet im Audit-Protokoll, das die Protokollzentrale als Quelle **Audit-Protokoll** zeigt. Das Audit-Protokoll wird auf der Box unabhängig von der Aufbewahrung der Protokollzentrale gehalten, und das Wiederherstellen einer Konfigurationssicherung ersetzt es nicht.

Manche Rechte gibt Pithyx nie: den Docker-Socket, einen privilegierten Container, alle Capabilities auf einmal oder die gefährlichen (etwa `SYS_ADMIN` oder `SYS_MODULE`) und Geräteknoten, die an die Box selbst reichen (Speicher, Datenträger und Partitionen, ZFS, Konsolen und Terminals, Eingabegeräte, Watchdogs). Eine App, die eines davon verlangt, wird mit [`app.elevated.unsupported`](/de/docs/reference/errors/#app.elevated.unsupported) abgelehnt. Ein Dienst mit dem Netzwerk der Box, ihren Prozessen oder einem Gerät (außer `/dev/net/tun`) läuft außerhalb des User-Namespace der Box mit dem eigenen Benutzer der App und der Gruppe des Geräts; eine solche App kann daher nie als root laufen ([`app.elevated.root_conflict`](/de/docs/reference/errors/#app.elevated.root_conflict)). Fehlt beim Start des Backends ein Gerät, etwa ein abgezogener Stick, startet das Backend nicht ([`app.device.unavailable`](/de/docs/reference/errors/#app.device.unavailable)); steck es ein und öffne die App erneut.

## Backends und Docker

Ein Backend ist eine Compose-Teilmenge mit Images, die per Digest festgelegt sind, geprüft gegen eine feste Allowlist. Die Box betreibt es in Docker (`docker.io` von Debian), das der Daemon nur startet, solange eine App mit Backend installiert und ihr Pool verfügbar ist. Container laufen in einem User-Namespace, in einem eigenen internen Netzwerk ohne Internet, außer die App hat die Berechtigung „Internet“, und erreichen keine Dienste des Hosts. Andere Geräte in deinem Netzwerk (Router, Drucker, andere Server) erreichen sie nur mit der Berechtigung „Lokales Netzwerk“; „Internet“ allein schließt sie nicht ein. Container, die nicht als root laufen, haben keine Linux-Capabilities. Ein Container als root ist nur erlaubt, wenn die App es angibt, und der Installationsdialog warnt davor. Rechte über diese Sandbox hinaus sind [Sonderrechte](#sonderrechte).

Ein Backend mit dem Lifecycle `always` läuft, wann immer es kann; `onDemand` startet, wenn ein Benutzer die App öffnet (eine Anfrage wartet bis zu 30 Sekunden), und hält 10 Minuten nach dem Schließen des letzten Fensters an. Ein fehlgeschlagener Start wird nach 1 Minute wiederholt. Die Ausgabe der Container geht ins Protokoll-Center als Quelle `app/<id>`.
