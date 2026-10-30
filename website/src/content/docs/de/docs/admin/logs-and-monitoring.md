---
title: 'Logs und Überwachung'
description: 'Das Protokoll-Center, der Ressourcenmonitor und Benachrichtigungen: was sie zeigen, wie lange Daten bleiben und was sie kosten.'
status: ready
audience: admin
sidebar:
  order: 12
---

Zwei Administrator-Apps zeigen, was die Box tut: das Protokoll-Center (Meldungen des Daemons, des Systems und der Apps) und der Ressourcenmonitor (CPU, Arbeitsspeicher, Netzwerk, Platten und Temperaturen). Benachrichtigungen erreichen Menschen im Desktop und über [Benachrichtigungskanäle](/de/docs/admin/notifications/) per E-Mail oder Webhook.

## Das Protokoll-Center

![Das Protokoll-Center im dunklen Design mit den Quellen, gruppiert nach System und Apps, und ihren Zählern, den Stufen mit der Zahl der Warnungen und Fehler, dem Zeitraum Letzte 24 Stunden, den Einträgen nach Tagen und dem Detailbereich einer Warnung](../../../../../assets/screenshots/logs-center.de.webp)

_Das Protokoll-Center mit seinen Quellen, Filtern für Stufe und Zeitraum und den Details des gewählten Eintrags._

Die Navigation links zeigt **Alle Quellen**, dann die Quellen unter **System** („Pithyx-Daemon“, „Audit-Protokoll“ mit den Bestätigungen und Sonderrechten von Apps, siehe [Apps und Store](/de/docs/admin/apps-and-store/#sonderrechte), „Reverse Proxy · {host}“ für das Zugriffsprotokoll eines Proxy-Hosts und die Journal-Units wie kernel, smbd, ssh, ZFS, systemd-networkd, timesyncd, Docker und smartmontools) und unter **Apps**; jede zeigt, wie viele Warnungen und Fehler sie im gewählten Zeitraum protokolliert hat. Die Werkzeugleiste filtert die Stufe (**Alle** ohne Debug, **Warnungen**, **Fehler** oder **Debug** für jede Stufe, mit ihren Zahlen) und den Zeitraum (letzte Stunde, 24 Stunden, 7 Tage, 30 Tage, gesamte Zeit) und durchsucht die Meldungen und ihre Felder (eine genaue Request-ID findet ihre Einträge auch). Das Protokoll-Center ist live: Neue Einträge erscheinen, sobald der Daemon sie schreibt, etwa einmal pro Sekunde, bis du **Anhalten** wählst; **Live** holt nach und folgt wieder. Die Einträge sind nach Tagen gruppiert; Meldung und Felder des gewählten Eintrags stehen rechts, ohne seine Request-ID. Die Statusleiste sagt, wie viele Einträge geladen sind und wie lange die Protokolle aufbewahrt werden. Seiten fassen standardmäßig 200 Einträge, höchstens 1000. **Exportieren** bietet **Als Text exportieren** (`.log`) und **Als JSON-Zeilen exportieren** (`.jsonl`), je bis zu 1 000 000 Einträge.

Logs liegen in einer Datenbank auf der Systemplatte unter `/var/log/pithyx/`, funktionieren also ohne Pool. Die Aufbewahrung stellst du mit **Aufbewahrung** ein: standardmäßig 30 Tage (1 bis 365) und höchstens 4 GB (64 MB bis 4 GB); die ältesten Einträge gehen zuerst. Das optionale Archiv auf einem Pool ist standardmäßig aus. Wenn es an ist, werden Einträge einmal am Tag auf den Pool kopiert, und zwar bevor die aktuellen Logs welche verwerfen, was die Platten des Pools etwa einmal am Tag weckt. Archivtage: 1 bis 3650 (Standard 365); Größe 0,25 bis 1024 GB (Standard 10 GB). Ist der Pool nicht verfügbar, warten Einträge auf der Systemplatte.

Der Daemon schreibt außerdem JSON-Dateien `/var/log/pithyx/daemon.json*`, die bei 20 MB rotieren, mit 10 Dateien. Geheimnisse werden in Logs maskiert.

App-Meldungen kommen über das SDK an: `info`, `warn` und `error` immer, `debug` nur, solange ein Administrator es in den Details der App eingeschaltet hat. Eine Meldung hat höchstens 4000 Zeichen und höchstens 20 Datenfelder; eine App darf 20 Meldungen pro Sekunde loggen (Spitze 200). Die Ausgabe von Backend-Containern landet in der Quelle `app/<id>`. Eine Zeile auf stdout ist Info, eine Zeile auf stderr eine Warnung, und eine Zeile, die ein JSON-Objekt mit einem Text `level` ist (`debug` oder `trace`, `info`, `warn` oder `warning`, `error`, `fatal` oder `critical`), behält diese Stufe. Eine solche JSON-Zeile zeigt ihr `message` (oder `msg`) als Meldung und ihre übrigen Felder (höchstens 32, jedes auf 1024 Zeichen gekürzt) als Daten; ein W3C-Feld `traceparent` verbindet sie mit der Anfrage, sodass die Suche nach der Request-ID sie neben den Zeilen des Daemons findet. Eine `debug`-Zeile bleibt nur, solange ein Administrator die Debug-Meldungen der App eingeschaltet hat.

## Der Ressourcenmonitor

![Der Ressourcenmonitor, Übersicht, mit CPU und ihren Kernen, Arbeitsspeicher mit dem ZFS-Cache, Netzwerk und den Platten während eines Scrubs und den Temperaturen](../../../../../assets/screenshots/monitor-performance.de.webp)

_Die Diagramme der letzten Stunde, hier mit einem Scrub in der letzten Viertelstunde._

Die Navigation des Ressourcenmonitors hat **Übersicht**, **CPU**, **Arbeitsspeicher**, **Platten**, **Netzwerk**, **Temperaturen** und **Prozesse**; die Werkzeugleiste stellt den Zeitraum der Diagramme ein (eine Minute, 10 Minuten oder eine Stunde), und die Statusleiste zeigt Laufzeit und Last. Die Übersicht zeigt CPU und Arbeitsspeicher, Netzwerk und Platten als Summe über alle Schnittstellen und Platten, die Temperaturen und die sechs Prozesse, die gerade am meisten CPU brauchen; die anderen Seiten zeigen ihren Teil, Netzwerk und Platten pro Gerät. Er misst jede Sekunde, und nur, solange jemand zuschaut, Prozesse nur, solange die Übersicht oder **Prozesse** gezeigt wird; der Verlauf wird eine Stunde lang im Arbeitsspeicher gehalten. Der Verlauf deckt also nur die Zeit ab, in der der Monitor irgendwo geöffnet war: Solange niemand zuschaut, wird nichts gemessen, und einen längeren Verlauf (einen Tag oder eine Woche) gibt es nicht.

Er zeigt die CPU insgesamt und pro Kern sowie den Arbeitsspeicher aufgeteilt in Programme, ZFS-Cache (ARC), Seiten-Cache und Frei, dazu Swap. Der ZFS-Cache gibt Speicher sofort ab, sobald Programme ihn brauchen. Der Netzwerkverkehr wird pro Schnittstelle gezeigt, Lesen, Schreiben und Auslastung pro Platte (aus den Zählern des Kernels, sodass eine schlafende Platte weiterschläft) und Temperaturen von den Sensoren; Plattentemperaturen werden hier nicht gelesen. Die Prozessliste zeigt Name, PID, Benutzer, CPU (Anteil aller Kerne zusammen) und Speicher, mit einem Filter. Einstellungen > **Info-Center** zeigt Version, Hostname, Kernel, CPU, Arbeitsspeicher und Laufzeit.

## Benachrichtigungen

Jeder Benutzer hat im Desktop eine Liste (bis zu 200 Einträge, 30 Tage aufbewahrt). Administratoren bekommen Speicherwarnungen (beeinträchtigte oder ausgefallene Pools, fehlende Platten, Datenfehler, 80 und 90 Prozent voll, ausfallende SMART-Werte), Job-Ergebnisse und die Store-Hinweise „App-Updates“ und „Katalog nicht abgerufen“. Apps mit der Berechtigung, Benachrichtigungen zu zeigen, können an den Benutzer senden, der sie nutzt. Administratoren erfahren außerdem von wiederholt falschen Anmeldungen (fünf für einen Namen oder dreißig von einer Adresse innerhalb von fünf Minuten), höchstens einmal pro Stunde je Adresse und Name. Kanäle senden all das per E-Mail oder Webhook: siehe [Benachrichtigungen per E-Mail und Webhook](/de/docs/admin/notifications/).
