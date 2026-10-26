---
title: 'Verschlüsselte Pools: wenn etwas schiefgeht'
description: 'Was zu tun ist, wenn ein verschlüsselter Pool gesperrt bleibt, das Einhängen scheitert, die Kopie des Schlüssels verloren ist, die Systemplatte fehlt oder die Box selbst kaputt ist.'
status: ready
audience: admin
since: '1.2.0'
sidebar:
  order: 4
---

Ein verschlüsselter Pool lässt sich nur mit seinem Schlüssel lesen ([Verschlüsselung](/de/docs/admin/storage/#verschlüsselung)). Diese Seite listet, was schiefgehen kann und wie du an die Daten kommst. Jeder Weg zurück braucht den Schlüssel: aus dem Schlüsseltresor, solange die Box läuft, oder aus der Kopie, die du beim Anlegen des Pools oder beim Ändern seines Schlüssels aufbewahrt hast. Ohne beides kann niemand die Daten lesen, auch Pithyx nicht.

## Der Pool bleibt nach einem Start gesperrt

Die Pool-Karte zeigt **Gesperrt**, und Administratoren bekommen die Benachrichtigung „Pool … ist gesperrt“ oder „Pool … ließ sich nicht entsperren“.

- **Nach jedem Start von Hand entsperrt**: Das ist so gewollt. Wähle **Entsperren…** und gib den Schlüssel ein.
- **Schlüssel im Schlüsseltresor, aber er hat den Pool nicht entsperrt**: Die Tresordatei ist beschädigt oder enthält einen älteren Schlüssel (zum Beispiel, nachdem der Schlüssel auf einer anderen Box geändert wurde). Am Pool wurde nichts verändert. Wähle **Entsperren…**, gib den Schlüssel aus deiner Kopie ein und lass **Schlüssel im Schlüsseltresor aufbewahren und den Pool bei jedem Start automatisch entsperren** angehakt: Der Tresor bekommt den richtigen Schlüssel, und der Pool entsperrt sich ab dem nächsten Start wieder von selbst.
- **Ein abgelehnter Schlüssel** mit [`storage.encryption.key_invalid`](/de/docs/reference/errors/#storage.encryption.key_invalid) hat einen Tippfehler: Die letzten Zeichen eines Schlüssels prüfen die übrigen. Bindestriche, Leerzeichen, Groß- oder Kleinschreibung und O für 0 oder I und L für 1 spielen keine Rolle. [`storage.encryption.key_wrong`](/de/docs/reference/errors/#storage.encryption.key_wrong) heißt, der Schlüssel ist gültig, gehört aber zu einem anderen Pool oder ist ein älterer Schlüssel dieses Pools.

## Entsperren hat geklappt, aber ein Ordner fehlt

Hat der Schlüssel den Pool geöffnet, ließ sich aber ein Dataset nicht einhängen, sagt der Dialog das, und der Fehler nennt das Dataset ([`storage.command.failed`](/de/docs/reference/errors/#storage.command.failed)); das Protokoll enthält die Meldung von ZFS. Der Pool ist trotzdem entsperrt. Drücke erneut auf **Entsperren**, um den Rest einzuhängen. Hängt ZFS ([`storage.command.timed_out`](/de/docs/reference/errors/#storage.command.timed_out)), antwortet vermutlich eine Platte des Pools nicht: Prüfe zuerst die Platten in Speicher.

Datasets, die einen Systemordner verdecken würden, werden nie eingehängt, wie bei einem [Import](/de/docs/admin/storage/#importieren-und-exportieren).

## Die aufbewahrte Kopie des Schlüssels ist verloren

Solange der Schlüssel im Schlüsseltresor liegt und die Box läuft, wähle **Schlüssel anzeigen…** auf der Pool-Karte. Es fragt nach deinem Passwort und, wenn die Zwei-Faktor-Anmeldung an ist, nach einem Code aus deiner Authenticator-App und zeigt den Schlüssel mit **Herunterladen** und **Drucken**. Bewahre ihn erneut auf. Jedes Mal, wenn ein Schlüssel angezeigt wird, hält das Protokoll fest, wer ihn gesehen hat (nie den Schlüssel selbst).

Noch besser: Gib dem Pool einen neuen Schlüssel. **Schlüssel ändern…** erzeugt einen, zeigt ihn und ändert den Schlüssel erst, wenn du zwei seiner Gruppen aus deiner aufbewahrten Kopie eingegeben hast.

Liegt der Schlüssel nicht im Tresor und ist die Kopie verloren, lassen sich die Daten nicht wieder lesen. Kopiere alles, was du brauchst, vom Pool, solange er noch entsperrt ist, und lege ihn dann neu an.

## Der Schlüsseltresor lässt sich nicht beschreiben

Ist die Systemplatte voll oder defekt, scheitert das Ablegen eines Schlüssels im Tresor mit [`storage.encryption.vault_failed`](/de/docs/reference/errors/#storage.encryption.vault_failed). Der Pool selbst ist in Ordnung und bleibt entsperrt; nach dem nächsten Start wartet er darauf, dass sein Schlüssel eingegeben wird. Behebe das Problem mit der Systemplatte und entsperre den Pool dann erneut oder ändere seinen Schlüssel mit angehaktem Tresor. Ein Schlüssel, den der Tresor nicht aufnehmen konnte, bleibt nie halb geschrieben zurück, und ein älterer Schlüssel dort wird entfernt, damit er beim nächsten Start nicht scheitert.

## Die Systemplatte wurde ersetzt oder Pithyx neu installiert

Der Schlüsseltresor liegt auf der Systemplatte und ist nie Teil einer [Konfigurationssicherung](/de/docs/admin/backup/). [Importiere](/de/docs/admin/storage/#importieren-und-exportieren) den Pool nach einer Neuinstallation; er bleibt gesperrt. Wähle **Entsperren…**, gib den Schlüssel aus deiner Kopie ein und hake die Tresor-Option an, wenn die Box ihn wieder bei jedem Start entsperren soll.

## Den Pool ohne Pithyx öffnen

Ist die Box kaputt und läuft Pithyx nicht mehr, lassen sich die Platten auf jedem Linux mit OpenZFS 2.x öffnen. Das heruntergeladene oder ausgedruckte Schlüsselblatt enthält den Schlüssel ein zweites Mal als 64 Hex-Zeichen, zusammen mit diesen Befehlen. Schließe alle Platten des Pools an und führe als root aus:

```sh
zpool import -f -N tank
zfs load-key tank/pithyx
zfs mount -a
```

Ersetze `tank` durch den Namen des Pools. `zfs load-key` fragt nach dem Schlüssel: Füge die Hex-Form ein. `-f` ist nötig, weil der Pool zuletzt von einem anderen System benutzt wurde; `-N` importiert ohne Einhängen, sodass du die Einhängepunkte vorher mit `zfs list -o name,mountpoint` prüfen kannst.

Dort funktioniert nur die Hex-Form; ZFS kennt die Form mit `PXPK-` nicht. Hast du nur diese Form, rechnet eine Box mit Pithyx sie um: **Entsperren…** nimmt sie, wie sie ist.

## Was nicht hilft

- Pithyx hat keinen Generalschlüssel, keinen Wiederherstellungsschlüssel für Pools und keinen Weg an einem verlorenen Schlüssel vorbei. Der Wiederherstellungsschlüssel aus der Einrichtung setzt das Passwort des Administrators zurück, nicht einen Pool-Schlüssel.
- Eine Konfigurationssicherung enthält nie Pool-Schlüssel.
- Wird ein Pool zerstört, wird auch sein Schlüssel aus dem Tresor gelöscht.
