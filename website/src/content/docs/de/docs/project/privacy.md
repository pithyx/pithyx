---
title: 'Datenschutz'
description: 'Pithyx und diese Webseite sammeln nichts: keine Telemetrie, keine Konten, keine Tracker.'
status: ready
sidebar:
  order: 2
---

Pithyx ist dafür gebaut, vollständig auf deinem eigenen Rechner zu laufen: Es hat keine Telemetrie, ruft nicht nach Hause, braucht kein Cloud-Konto und hängt von keinem Dienst ab, den das Projekt betreibt. Logs bleiben auf der Box. Diese Webseite hat keine Analyse und lädt nichts von Dritten.

## Die Box

- Keine Telemetrie, keine Nutzungsstatistiken und kein Konto beim Projekt. Logs werden nur auf die Box geschrieben (siehe [Logs und Überwachung](/de/docs/admin/logs-and-monitoring/)). Der Dienst des Daemons schaltet die Telemetrie der .NET-Kommandozeile aus.
- Pithyx wird frisch installiert; es importiert keine Daten aus anderen Systemen.
- Pithyx selbst baut diese ausgehenden Verbindungen auf:
  - **Systemupdates.** Einmal am Tag liest die Box die Kanaldatei ihrer Update-Quelle (standardmäßig die Releases des Projekts auf GitHub) und die Paketlisten von Debian; ein Update, das du installierst, lädt seine Datei von dort.
  - **Kataloge.** Der Store holt jeden aktivierten Katalog mit Git (standardmäßig den offiziellen Katalog des Projekts auf GitHub, täglich geprüft) und lädt App-Images aus der Registry, die im Katalog genannt ist.
  - **Zeitserver.** Ist die Liste der Zeitserver unter Einstellungen > **Regionale Einstellungen** leer, nutzt der Zeitdienst des Systems (systemd-timesyncd) seine Debian-Standardwerte, das sind öffentliche Server.
  - **Apps.** Eine App mit der Berechtigung `network.internet` kann das Internet erreichen; Apps ohne sie nicht. Andere Geräte in deinem Netzwerk erreicht nur eine App mit der Berechtigung `network.lan`.
- Ein Administrator kann die tägliche Update-Prüfung und Kataloge ausschalten und eigene Zeitserver setzen. Siehe [Was die Box verlässt](/de/docs/admin/security/#was-die-box-verlässt) und [Apps und Store](/de/docs/admin/apps-and-store/).

## Diese Webseite

Die Webseite hat keine Analyse und keine Tracking-Skripte und lädt ihre Schriften und alle anderen Dateien von ihrem eigenen Host: Es gibt weder einen externen Schriftdienst noch ein CDN.

## Support-Bundles

Pithyx hat noch keine Support-Bundles.
