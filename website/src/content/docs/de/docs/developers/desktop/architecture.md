---
title: 'Architektur des Desktops'
description: 'Der React-Web-Desktop: Shell, Fenstermanager, Kern-Apps und wie er mit dem Daemon spricht.'
status: ready
audience: developer
sidebar:
  order: 1
---

Der Desktop ist eine React-19-Single-Page-App in `desktop/` (Vite 8, TypeScript, zustand, TanStack Query, React Aria Components). Er spricht mit dem Daemon über den generierten REST-Client und einen WebSocket, läuft ohne erhöhte Rechte und hostet sowohl die eingebauten Kern-Apps als auch Apps von Drittanbietern in Fenstern mit Sandbox.

## Überblick

- **Stack.** React 19 mit dem React Compiler (kein handgeschriebenes Memoisieren), Vite 8, zustand für den Fenstermanager, TanStack Query für REST-Daten, i18next mit ICU-Nachrichten für Texte.
- **Quellordner** in `desktop/src`: `shell`, `wm`, `desktop`, `items`, `apps`, `realtime`, `api`, `platform`, `theme`, `design`, `errors`, `auth`, `installer`, `network` und `ui`.
- **Start** (`main.tsx`): das gespeicherte Theme und die Darstellung anwenden, den Query-Client anlegen, die Übersetzungen laden, die App rendern.
- **Ladepfad.** Der Anmeldebildschirm ist ein kleiner Chunk. Der Desktop-Chunk lädt, wenn du dich anmeldest (oder sofort, wenn ein Sitzungshinweis existiert), und der Anmeldebildschirm lädt ihn vor. Der Desktop liegt absichtlich nicht hinter `lazy()` und `Suspense`. Jede Kern-App lädt bei der ersten Nutzung, nie bei der Anmeldung.
- **Installer.** Die Oberfläche des Installers wird nur auf dem Live-System geladen.
- **Design-Galerie.** `desktop/design.html` (`pnpm design`, Port 5174) zeigt jeden Bildschirm für Layout-Prüfungen. Sie dient nur der Entwicklung.
- **Dev-Server.** Er leitet `/api` und `/ws` an den Daemon weiter und schreibt den `Origin`-Header von WebSocket-Upgrades um, weil der Daemon nur seinen eigenen Origin akzeptiert.
- **Sichere Kontexte.** Der Desktop funktioniert über reines HTTP per Adresse. ESLint lehnt `crypto.randomUUID`, `crypto.subtle`, die Clipboard-API, Service Worker, WebAuthn, Benachrichtigungen, `window.open` und ähnliche APIs außerhalb von `desktop/src/platform/browser.ts` ab, das Fallbacks bietet (`randomId`, `copyText`, `openInNewTab`).
- **Texte.** ESLint lehnt außerdem String-Literale in JSX (`i18next/no-literal-string`) und Importe von Icon-Bibliotheken ab, die nicht über `ui/icons.ts` laufen.

## Shell und Fenstermanager

Die Shell (`desktop/src/shell`) hat die Taskleiste, das Pithyx-Menü (Startmenü), den App-Umschalter, Fenster, Hintergrundbild, Benachrichtigungen, die Schaltfläche für Übertragungen und Haftnotizen. Die Taskleiste kann unten, oben, links oder rechts sitzen und wird mit dem Layout gespeichert.

Der Fenstermanager ist ein zustand-Store (`useDesktop`). Jede Änderung läuft über `dispatch(action)` und einen reinen `reduce()`.

- **Größenklassen** nach Breite des Viewports: kompakt unter 600 px, mittel von 600 bis 1023 px, erweitert ab 1024 px. Arbeitsflächen ab 2400 px bieten das Einrasten auf Drittel und Zwei-Drittel.
- **Platzierung** eines Fensters ist frei, maximiert oder eingerastet (links, rechts, Drittel, Zwei-Drittel).
- **Limit:** 50 Fenster. Der Daemon setzt dasselbe Limit durch.
- **Instanzen.** Eine App deklariert `single` oder `multi`. Wird eine `single`-App erneut geöffnet, fokussiert das das Fenster des Benutzers auf diesem Gerät und übergibt Startargumente.
- **Tastatur.** Alle Fenster-Shortcuts nutzen Alt+Umschalt, weil Browser und Betriebssysteme sie nicht belegen: Leertaste für das Fenstermenü, Links und Rechts zum Einrasten, Hoch zum Maximieren, Runter zum Wiederherstellen oder Minimieren, Bild auf und Bild ab zum Wechseln zwischen Fenstern.
- **Layout-Gedächtnis.** Das Layout (Fenster, Positionen, Reihenfolge, Taskleiste) wird pro Benutzer und Gerät im Daemon gespeichert (`PUT /api/desktop/layout` mit `If-Match`), um 1 Sekunde entprellt, nur vom sichtbaren Tab, und geschrieben, wenn die Seite ausgeblendet wird. Fenster von Apps, die du nicht mehr öffnen darfst, werden beim Wiederherstellen übersprungen. Limits: 50 Fenster, 16 KB Fensterargumente, 48 KB für das gesamte Layout. "Desktop holen von" kopiert das Layout eines anderen Geräts.
- **Darstellung.** Akzent und Hintergrundbild werden pro Benutzer gespeichert und über Geräte hinweg geteilt; die Wahl zwischen hell und dunkel wird pro Browser gespeichert.
- **Desktop-Elemente** (Verknüpfungen und Notizen) werden pro Benutzer über `/api/desktop/items` gespeichert.

## Kern-Apps

Kern-Apps sind in `desktop/src/apps/registry.tsx` aufgeführt; ihre IDs stehen in `apps/ids.ts` und sind im Daemon gespiegelt (`Apps/CoreApps.cs`).

| App-ID                | Name              | Wer sie sieht                                      | Instanzen |
| --------------------- | ----------------- | -------------------------------------------------- | --------- |
| `org.pithyx.files`    | Dateien           | berechtigte Benutzer                               | multi     |
| `org.pithyx.storage`  | Speicher          | Administratoren                                    | single    |
| `org.pithyx.settings` | Einstellungen     | Administratoren                                    | single    |
| `org.pithyx.monitor`  | Ressourcenmonitor | Administratoren                                    | single    |
| `org.pithyx.logs`     | Protokoll-Center  | Administratoren                                    | single    |
| `org.pithyx.viewer`   | Betrachter        | berechtigte Benutzer, nur mit einer Datei geöffnet | multi     |
| `org.pithyx.about`    | Über Pithyx       | alle                                               | single    |
| `org.pithyx.store`    | Store             | Administratoren                                    | single    |

Kern-Apps nutzen dieselbe `@pithyx/sdk`-API wie Apps von Drittanbietern, aber im eigenen Kontext des Desktops: `CoreSdkApp` verbindet das Fenster über ein prozessinternes Paar von Endpunkten statt über einen `MessageChannel` mit dem Host. Die App bekommt dasselbe `PithyxApp`-Objekt. Kern-Apps dürfen zusätzlich interne Host-Methoden nutzen, die nicht Teil des öffentlichen SDK sind. Apps von Drittanbietern laufen in einem `<iframe sandbox="allow-scripts allow-forms">` ohne `allow-same-origin`; ihre Identität stammt vom Message-Port, nie aus den Nachrichtendaten.

So fügst du eine Kern-App hinzu: Füge ihre ID zu `apps/ids.ts` und zu `CoreApps.cs` des Daemons hinzu, ergänze einen Eintrag in `registry.tsx`, füge `apps.<id>` in `i18n/en/desktop.json` und `i18n/de/desktop.json` hinzu (ein Test erzwingt das) und schreibe eine Fensterkomponente, die `CoreSdkApp` umhüllt.

## Daten und Echtzeit

- **REST.** Ein Client, `api = createApiClient()`, aus `@pithyx/api-client`. `unwrap(await api.GET(...))` wirft einen `PithyxApiError` mit Code, Parametern, Request-ID und Status. Der Query-Client wiederholt fehlgeschlagene Abfragen bis zu 3 Mal, und ein Fehler `auth.session.expired` schickt den Benutzer zurück zur Anmeldung.
- **WebSocket.** Eine `RealtimeConnection` pro Tab unter `/ws`. Abonnements werden per Referenz gezählt und nach jedem erneuten Verbinden neu gesendet. `useTopic(topic)` liefert den neuesten Snapshot (undefined, bis der erste eintrifft); `realtime.listen` behandelt Mitglieder von Topic-Familien wie einen einzelnen Ordner.
- **Reducer.** `reducers.ts` hat einen Reducer pro Topic und ist durch die generierte Topic-Liste typisiert, sodass ein neues Topic des Daemons den Build brechen lässt, bis der Desktop es behandelt.
- **Neu verbinden.** Die Wartezeit ist `min(30 s, 0.5 s * 2^attempt)`, zufällig auf 50 bis 100 Prozent verteilt. Close-Code 4401 schickt den Benutzer zur Anmeldung, 4403 hält an, alle anderen verbinden neu. Siehe [API](/de/docs/developers/daemon/api/).
- **Fehler.** Fehlermeldungen vom Socket gehen in den Fehlerdialog. Request-IDs werden in die Konsole geschrieben, nie angezeigt.
- **Auflistungen.** Ordnerlisten laden in Cursor-Seiten über REST. Pithyx streamt Auflistungen nicht über den WebSocket und sortiert nicht in einem Web Worker.
