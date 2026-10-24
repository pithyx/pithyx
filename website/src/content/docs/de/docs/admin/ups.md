---
title: 'USV'
description: 'Die Box bei einem Stromausfall mit einer USV an USB oder einer USV eines anderen NUT-Servers sauber herunterfahren, und wann sie herunterfährt.'
status: draft
audience: admin
since: 1.2.0
sidebar:
  order: 13
---

Einstellungen > **USV** verbindet die Box mit einer unterbrechungsfreien Stromversorgung. Bei einem Stromausfall benachrichtigt die Box ihre Administratoren und fährt sauber herunter, bevor der Akku leer ist: Apps und Dateidienste stoppen, und die Pools werden ausgehängt. Pithyx liest die USV über NUT (Network UPS Tools) und funktioniert deshalb mit den USV-Modellen, die NUT unterstützt.

## Die USV wählen

- **USV an USB**: Schließ die USV mit ihrem USB-Kabel an und wähle das. Die Box betreibt dann einen eigenen NUT-Server nur für sich (auf `127.0.0.1`) mit dem Treiber `usbhid-ups`, der die USV selbst findet. Sagt der Zustand, dass keine USV gefunden wurde, prüf das Kabel oder ob die USV einen anderen NUT-Treiber braucht (noch nicht wählbar).
- **USV eines anderen NUT-Servers**: Ein anderer Rechner (ein NAS, ein Router oder ein Server mit NUT) hat die USV. Gib seine Adresse ein, den Port (3493, wenn nicht geändert), den Namen der USV aus seiner `ups.conf` und einen Benutzer aus seiner `upsd.users` mit `upsmon secondary`. Die Box meldet sich als Secondary an, damit dieser Rechner auf die Box wartet, bevor er den Strom abschaltet. Ohne Benutzer liest die Box die USV nur.

Das Passwort wird nie wieder angezeigt; lass das Feld leer, um es zu behalten.

## Wann die Box herunterfährt

Die Box liest die USV alle 5 Sekunden. Auf Akku fährt sie herunter, sobald eines davon zutrifft:

- die Ladung liegt bei oder unter der **Akku-Grenze** (standardmäßig 20 Prozent, 5 bis 90),
- die Restlaufzeit liegt bei oder unter der **Laufzeit-Grenze** (standardmäßig 5 Minuten, 1 bis 60),
- die USV meldet einen schwachen Akku,
- der Primary des NUT-Servers fährt alle Systeme herunter (erzwungenes Herunterfahren),
- die USV lief auf Akku und hat 15 Sekunden nicht geantwortet.

Die Administratoren bekommen eine Benachrichtigung, wenn die USV auf Akku geht, wenn der Strom zurück ist, vor dem Herunterfahren und wenn die USV eine Minute lang nicht erreichbar ist.

Nach dem Herunterfahren schaltet eine USV an USB ihren eigenen Strom ab, damit die Box wieder startet, sobald der Strom zurück ist, wenn ihre Firmware beim Wiederkehren des Stroms startet. Kommt der Strom während des Herunterfahrens zurück und die USV lässt ihn an, startet die Box nach 10 Minuten neu.

## Ohne USV ausprobieren

Der NUT-Treiber `dummy-ups` spielt eine USV aus einer Datei, so lassen sich die Einstellungen mit einem NUT-Server auf einem anderen Linux-Rechner ausprobieren: ein Abschnitt `[sim]` mit `driver = dummy-ups` und `port = sim.dev` in seiner `ups.conf`, wobei `sim.dev` Zeilen wie `ups.status: OL` und `battery.charge: 100` enthält. Werden sie zu `ups.status: OB` und einer niedrigeren Ladung geändert, spielt das einen Stromausfall; unter den Grenzen fährt die Box wirklich herunter.
