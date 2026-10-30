---
title: 'Apps benutzen'
description: 'Apps öffnen, mehrere Fenster derselben App und was eine App von deinen Daten sehen darf.'
status: ready
audience: user
sidebar:
  order: 4
---

Apps öffnen sich in Fenstern auf dem Desktop, wie Dateien oder Einstellungen. Einige Apps gehören zu Pithyx; Administratoren können im Store weitere installieren. Jede App läuft in einer Sandbox und sieht nur, was ein Administrator ihr erlaubt hat, begrenzt durch das, was dein eigenes Konto darf.

## Eine App öffnen

Öffne das Pithyx-Menü und klicke auf eine App, oder tippe einen Teil ihres Namens in das Suchfeld. Du kannst eine App auch über eine Verknüpfung auf dem Desktop öffnen oder dort eine ablegen mit **Zum Desktop hinzufügen**. Siehe [Der Desktop](/de/docs/user/desktop/).

Die Apps, die zu Pithyx gehören, sind **Dateien**, **Betrachter**, **Einstellungen**, **Ressourcenmonitor**, **Protokoll-Center**, **Über Pithyx** und **Store**. Du siehst nur die Apps, die du nutzen darfst:

- **Dateien** und **Betrachter** stehen neuen Benutzern standardmäßig zur Verfügung.
- **Einstellungen**, **Ressourcenmonitor**, **Protokoll-Center** und **Store** sind für Administratoren. Einstellungen öffnet sich mit der **Übersicht**: die Box mit Version, Hardware, Laufzeit und Updates, dann **Braucht Aufmerksamkeit** mit einer Zeile je Problem, das die Box kennt (eine Warnung eines Pools, ein Update von Pithyx oder einer App, ein Gerät, das der Box noch nicht vertraut), und dem Knopf, der zur Lösung führt, dann **Alle Einstellungen** als Kacheln. Die Navigationsspalte links listet jede Seite unter **System** und **Zugriff** und hat eine Suche, die eine Seite auch über ihren Inhalt findet, etwa `ntp`.
- **Über Pithyx** steht allen offen.
- Apps, die ein Administrator installiert hat, erscheinen bei den Benutzern, die der Administrator gewählt hat. Frag einen Administrator, wenn dir eine App fehlt.

Liegen die Daten einer App auf einem Pool, der offline oder gesperrt ist, lässt sich die App erst öffnen, wenn der Pool wieder da ist.

## Ein Fenster und mehrere Fenster

Eine App entscheidet, wie viele Fenster sie haben darf:

- **Ein Fenster:** Öffnest du die App erneut, kommt ihr Fenster nach vorn, statt ein zweites zu öffnen. Das gilt pro Gerät, ein anderer Browser hat also sein eigenes Fenster. Einstellungen, Ressourcenmonitor, Protokoll-Center, Über Pithyx und der Store funktionieren so.
- **Mehrere Fenster:** Jeder Klick öffnet ein neues Fenster. Dateien und Betrachter funktionieren so.

Fenster kommen zurück, nachdem du die Seite neu geladen oder dich erneut angemeldet hast. Siehe [Layout pro Gerät](/de/docs/user/desktop/#layout-pro-gerät). Insgesamt kannst du 50 Fenster offen haben.

## Worauf eine App zugreifen darf

Eine App, die du aus dem Store installiert hast, läuft in einem eigenen Frame, ohne Zugriff auf den Rest des Desktops, auf deine Cookies oder dein Passwort. Sie spricht mit Pithyx nur über einen kontrollierten Kanal. Ein Administrator sieht vor der Installation, was eine App anfragt. Die möglichen Berechtigungen sind:

| Berechtigung                      | Was sie der App erlaubt                                                                      |
| --------------------------------- | -------------------------------------------------------------------------------------------- |
| **Dateien lesen**                 | Dateien in den freigegebenen Ordnern lesen, die du nutzen darfst                             |
| **Dateien ändern**                | Dateien in den freigegebenen Ordnern anlegen, ändern und löschen, in die du schreiben darfst |
| **Freigegebene Ordner sehen**     | Sehen, welche freigegebenen Ordner du nutzen darfst                                          |
| **Benachrichtigungen**            | Diesem Benutzer Benachrichtigungen zeigen                                                    |
| **Alle Nutzer und Gruppen sehen** | Die Namen aller Benutzer und Gruppen auf der Box sehen                                       |
| **Internet**                      | Sich mit Servern im Internet verbinden. Ohne sie bleibt die App auf dieser Box.              |

Gut zu wissen:

- Eine App handelt mit deinen Rechten. Sie sieht nie einen freigegebenen Ordner, den du nicht nutzen kannst, und sie schreibt nicht dort, wo du nur lesen darfst. Mit **Dateien lesen** und **Dateien ändern** kann sie die ganze Datei-API der Box in deinen freigegebenen Ordnern nutzen, nicht nur Dateien, die du ausgewählt hast.
- Ein Administrator kann einer App eine Berechtigung entziehen. Die Änderung erreicht offene Fenster innerhalb weniger Minuten.
- Entzieht dir ein Administrator das Recht, eine App zu nutzen, schließen sich ihre Fenster.
- Apps mit einem Backend betreiben einen eigenen Dienst in Containern auf der Box. Je nach App läuft das Backend die ganze Zeit, oder es startet, wenn jemand die App öffnet, und stoppt wieder, nachdem das letzte Fenster geschlossen wurde (10 Minuten später, sofern die App keine andere Zeit festlegt).
- Apps, die aus einer Datei installiert wurden, prüft das Projekt nicht. Administratoren sollten nur Apps von Entwicklern installieren, denen sie vertrauen.

Für Administratoren: [Apps und Store](/de/docs/admin/apps-and-store/).

## Wenn eine App nicht startet

- **Das Fenster schließt sich sofort, oder die App ist weg.** Ein Administrator hat sie möglicherweise deaktiviert oder entfernt oder dir das Recht entzogen, sie zu nutzen. Frag einen Administrator.
- **Die App ist beim ersten Start langsam.** Ein Backend, das gestoppt war, braucht einen Moment zum Starten. Eine Anfrage an es wartet bis zu 30 Sekunden. Schlägt sie trotzdem fehl, siehst du „Das Backend der App läuft nicht oder antwortet nicht. Versuch es gleich noch einmal.“
- **Eine Meldung erwähnt den Container-Dienst** oder dass das Backend nicht starten konnte. Ein Administrator kann das Protokoll der App im Protokoll-Center lesen.
- **Eine Meldung sagt, dass der Ordner für den Speicher der App nicht bereit ist.** Der Pool mit den Daten der App fehlt, ist offline oder gesperrt. Niemand kann die App öffnen, bis er wieder da ist.
- **Der Zugang der App ist abgelaufen** („Schließ die App und öffne sie erneut.“). Schließe das Fenster und öffne die App erneut.
- **Die Verbindung zwischen der App und dem Desktop ist geschlossen.** Öffne die App erneut.
- **Die App antwortet nicht.** Nach fünf Sekunden fragt ein Dialog, ob du **Warten** oder **Trotzdem schließen** willst. Beim Schließen gehen nicht gespeicherte Änderungen verloren.
- **Die App braucht eine neuere Version von Pithyx.** Frag einen Administrator.
