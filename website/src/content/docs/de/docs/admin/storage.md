---
title: 'Speicher und Pools'
description: 'Festplatten, ZFS-Pools, Datasets, einen Pool importieren und erweitern, und warum manche Platten abgelehnt werden.'
status: ready
audience: admin
sidebar:
  order: 3
---

Die Einstellungen (nur für Administratoren) haben die Gruppe **Speicher** mit den Seiten **Pools**, **Platten**, **Zustand und Wartung** und **Speicher für Fortgeschrittene**. Dort legst du die ZFS-Pools an, die alle Daten der Box enthalten, und verwaltet sie. Pithyx nutzt die Systemplatte nie für einen Pool, zeigt für jede Platte, ob sie verwendet werden darf, und verlangt die Eingabe der Seriennummer, bevor eine Platte mit Daten gelöscht wird. Änderungen laufen im Hintergrund, und **Befehle anzeigen** listet die genauen Befehle auf, bevor etwas ausgeführt wird.

## Bewertung der Platten

![Speicher, Platten, mit der Spalte Für einen neuen Pool: zwei Systemplatten Nicht verfügbar, vier leere Platten Verfügbar und eine Platte mit Partitionen Bestätigung nötig](../../../../../assets/screenshots/storage-disks.de.webp)

_Der Reiter Platten sagt für jede Platte, ob ein neuer Pool sie verwenden darf._

Die Spalte **Für einen neuen Pool** zeigt pro Platte eine von drei Bewertungen: **Verfügbar**, **Bestätigung nötig** oder **Nicht verfügbar**. Pithyx prüft die Bewertung direkt vor jedem Befehl eines Plans erneut, sodass eine Platte, die sich nach deinem Blick geändert hat, erkannt wird.

Eine Platte ist **Nicht verfügbar**, wenn:

- sie das System enthält (`/`, `/boot`, `/boot/efi` oder `/var/lib/pithyx`, auch über Partitionen, LVM, md oder einen anderen Pool): „Enthält das System. Wird nie für Pools verwendet.“
- sie schreibgeschützt ist oder ein Dateisystem darauf in Benutzung ist;
- sie schon zu einem Pool gehört;
- eine andere Platte dieselbe Seriennummer meldet, sodass sich beide nicht unterscheiden lassen;
- sie Daten enthält, aber keine Seriennummer zum Bestätigen hat.

Bei einer Platte ist **Bestätigung nötig**, wenn sie Partitionen, Dateisysteme oder andere Signaturen hat oder noch nicht geprüft wurde. Bei einer Platte mit Inhalt sagt Pithyx „Enthält Daten“ und listet die gefundenen Partitionen, Dateisysteme und Signaturen auf. Gib ihre Seriennummer genau so ein, wie sie angezeigt wird, um sie zu verwenden. Leere Platten sind **Verfügbar**. Eine Ersatzplatte (siehe unten) braucht immer ihre Seriennummer, auch wenn sie leer ist.

:::danger[Alles auf einer bestätigten Platte geht verloren]
Die Bestätigung mit der Seriennummer löscht die Platte: Pithyx führt bei Pool-Mitgliedern `zpool labelclear -f` aus, danach `wipefs --all` auf den Partitionen und der Platte. Es gibt kein Rückgängigmachen. Vergleiche die Seriennummer mit dem Aufkleber auf der Platte.
:::

Wenn sich eine Platte geändert hat, nachdem der Plan angezeigt wurde, wird nichts geändert, und du bekommst [`storage.plan.stale`](/de/docs/reference/errors/#storage.plan.stale). Ein Plan läuft nach 10 Minuten ab ([`storage.plan.expired`](/de/docs/reference/errors/#storage.plan.expired)); zeige ihn dann erneut an.

## Einen Pool anlegen

![Der Assistent Pool anlegen bei Schritt 2, Aufbau, für vier Platten mit empfohlenem RAID-Z1 und den Karten Spiegel, Spiegelpaare, RAID-Z1, RAID-Z2 und Stripe](../../../../../assets/screenshots/storage-layout.de.webp)

_Schritt 2 des Assistenten vergleicht die Aufbauten für die gewählten Platten und markiert den empfohlenen._

Öffne Einstellungen > **Pools** und wähle **Pool anlegen…**. Der Assistent hat vier Schritte: **Platten**, **Aufbau**, **Verschlüsselung** und **Name und Prüfung**, und für einen verschlüsselten Pool einen fünften, **Schlüssel** (siehe [Verschlüsselung](#verschlüsselung)). Platten werden über ihre Namen unter `/dev/disk/by-id` angesprochen, sodass ein Pool einen geänderten Anschluss oder ein anderes Kabel übersteht.

| Aufbau                            | Mindestanzahl Platten | Hinweise                                    |
| --------------------------------- | --------------------- | ------------------------------------------- |
| Einzelne Platte (keine Redundanz) | 1                     | genau eine Platte                           |
| Stripe                            | 2                     | fällt eine Platte aus, ist alles verloren   |
| Spiegel                           | 2                     |                                             |
| Spiegelpaare                      | 4                     | eine gerade Anzahl Platten                  |
| RAID-Z1 / RAID-Z2 / RAID-Z3       | 3 / 4 / 5             | eine / zwei / drei Platten dürfen ausfallen |

Der Assistent empfiehlt einen Aufbau: bei einer Platte einzeln, bei zwei Platten Spiegel, bei drei bis fünf Platten RAID-Z1, bei sechs oder mehr RAID-Z2. Bei Platten unterschiedlicher Größe zählt jede Platte so groß wie die kleinste, und der Assistent sagt dir, wie viel ungenutzt bleibt.

Der Pool-Name beginnt mit einem Kleinbuchstaben, danach folgen Kleinbuchstaben, Ziffern, `-` oder `_`, höchstens 32 Zeichen. Die Namen `mirror`, `raidz`, `raidz1` bis `raidz3`, `draid`, `spare`, `log`, `cache`, `special`, `dedup` und `pithyx` sowie Namen, die mit `c0` beginnen, sind reserviert.

Jeder neue Pool bekommt dieselben Optionen: `ashift=12`, `compression=lz4`, `xattr=sa`, `acltype=posixacl`, `atime=off`, `normalization=formD`. Danach legt Pithyx seine Dataset-Struktur unter `<pool>/pithyx` an: `shares`, `homes`, `apps`, `system` und `cache` (Kontingent 2,5 GiB, keine Kompression, keine automatischen Snapshots). Ein Pool, den Pithyx nicht angelegt hat, hat kein Dataset `pithyx`, und Pithyx legt darauf nichts an ([`storage.pool.not_prepared`](/de/docs/reference/errors/#storage.pool.not_prepared)).

Weitere Pool-Aktionen: **Ersetzen…** (fragt immer nach der Seriennummer), **Hot Spare hinzufügen…**, **Spare entfernen** und **Pool löschen…**.

:::danger[Einen Pool löschen]
**Pool löschen…** listet jedes Dataset auf, das mit dem Pool verschwindet, und verlangt die Eingabe des Pool-Namens ([`storage.pool.name_mismatch`](/de/docs/reference/errors/#storage.pool.name_mismatch), wenn er abweicht). Löschen und Exportieren werden abgelehnt, solange Platten fehlen ([`storage.pool.disks_missing`](/de/docs/reference/errors/#storage.pool.disks_missing)) oder der Pool angehalten ist.
:::

Ein Pool zeigt einen dieser Zustände: Intakt, Beeinträchtigt, Ausgefallen, Offline, Nicht erreichbar, Entfernt, Angehalten, Unbekannt oder „Nicht angeschlossen“. Bei einem beeinträchtigten Pool wird eine Platte, die zurückkommt, von selbst neu aufgebaut (Resilver). Der Daemon holt eine zurückkehrende Platte mit `zpool online` und danach `zpool clear` zurück, jeweils mit 30 Sekunden Zeitlimit.

## Datasets und Wiederherstellungspunkte

Unter **Erweitert** listet **Datasets** > **Datasets anzeigen** die Datasets mit Name, Belegt, Frei, Limit, Kompression und dem Ordner oder der Größe. Dataset-Namen bestehen aus Buchstaben, Ziffern, `.`, `-` und `_`, beginnen mit einem Buchstaben oder einer Ziffer und sind höchstens 64 Zeichen lang.

- **Umbenennen…** verschiebt den Ordner; freigegebene Ordner folgen.
- **Grenzen…** setzt Kontingent, Reservierung, Kompression und Record-Größe. Ein leeres Kontingent oder eine leere Reservierung bedeutet kein Limit. Kompression: aus, lz4, zle, zstd (Stufen 1 bis 19) oder gzip (1 bis 9). Record-Größe: eine Zweierpotenz von 4 KiB bis 1 MiB, Standard 128 KiB.
- **Löschen…** verlangt den vollständigen Dataset-Namen und listet untergeordnete Datasets und Snapshots auf.

Datasets für Home-Ordner, Apps und das System lassen sich weder umbenennen noch löschen ([`storage.dataset.managed`](/de/docs/reference/errors/#storage.dataset.managed)), und ein Dataset, das ein freigegebener Ordner nutzt, muss zuerst diesen freigegebenen Ordner verlieren. Pithyx legt noch keine Volumes (Blockgeräte) an: In dieser Version nutzt keins, denn iSCSI und virtuelle Maschinen gibt es noch nicht. Vorhandene Volumes auf einem Pool erscheinen in der Tabelle und unter Erweitert, wo du sie löschen kannst.

Speicher hat keine Seite für Snapshots. Snapshots erscheinen in der Oberfläche als Wiederherstellungspunkte: ein Schalter pro freigegebenem Ordner unter Einstellungen > **Freigegebene Ordner**, der einmal am Tag einen Snapshot des Datasets des Ordners macht und die neuesten 7 behält, und **Frühere Versionen** in Dateien, um ältere Versionen zurückzuholen. Siehe [Freigegebene Ordner](/de/docs/admin/shared-folders/). Snapshots von Hand, Festhalten, das Zurücksetzen eines Datasets und andere Zeitpläne gibt es in der Oberfläche nicht.

## Importieren und exportieren

**Pool importieren…** durchsucht die Labels aller Platten, wenn du **Platten durchsuchen** drückst; die Suche kann schlafende Platten wecken und läuft nie von selbst. Ein Pool wird zuerst schreibgeschützt als Vorschau importiert (angezeigt als „Schreibgeschützt (Vorschau)“): Es wird nichts eingehängt und nichts geändert. Wähle dann **Zur Nutzung importieren…**, was ihn erneut importiert und die Datasets einhängt, die sicher einzuhängen sind, oder **Exportieren**, um die Vorschau zu beenden ([`storage.import.preview_required`](/de/docs/reference/errors/#storage.import.preview_required), wenn du die Vorschau überspringst).

Datasets, die sich nicht sicher einhängen lassen, bleiben ausgehängt (`canmount=noauto`) und nennen einen Grund: Sie würden einen Systemordner überdecken, etwas verdecken, das dort eingehängt ist, einen Ordner mit einem anderen Dataset teilen oder sind verschlüsselt und gesperrt. Freigegebene Ordner und Apps darauf halten an.

Ein Pool, der zuletzt von einem anderen System benutzt wurde, zeigt „Von einem anderen System benutzt“. Er braucht **Trotzdem importieren (erzwingen)** und die Eingabe des Pool-Namens ([`storage.import.force_required`](/de/docs/reference/errors/#storage.import.force_required)). Ist der Name vergeben, wähle **Neuer Name für den Pool**. **Exportieren…** hängt die Datasets aus und listet auf, was dadurch nicht mehr verfügbar ist; die Daten bleiben auf den Platten, und der Pool kann in ein anderes System umziehen.

## Einen Pool erweitern

**Platten hinzufügen…** fügt dem Pool eine neue Gruppe von Platten (ein Vdev) hinzu: einzeln, Spiegel (2 Platten), RAID-Z1 (3), RAID-Z2 (4) oder RAID-Z3 (5). Der Dialog warnt, wenn die neue Gruppe die Redundanz senkt, wenn sich ihr Aufbau von den vorhandenen unterscheidet (dafür muss der Pool-Name eingegeben werden) und dass sich die Platten nie wieder aus dem Pool entfernen lassen.

**Um eine Platte erweitern…** erweitert eine RAID-Z-Gruppe um eine Platte. Der Pool bleibt nutzbar, der Schritt ist nicht umkehrbar und verlangt den Pool-Namen, es läuft immer nur eine Erweiterung ([`storage.pool.expanding`](/de/docs/reference/errors/#storage.pool.expanding)), und Daten, die schon auf dem Pool liegen, behalten ihr altes Verhältnis von Daten zu Parität, bis sie neu geschrieben werden.

:::note
Speicher hat keine Schaltfläche, um eine einzelne Platte später in einen Spiegel zu verwandeln.
:::

## Verschlüsselung

Ein Pool wird beim Anlegen verschlüsselt oder nie: Der Assistent fragt „Soll der Pool verschlüsselt werden?“, und nachträglich lässt sich ein Pool nicht verschlüsseln. **Mit einem Schlüssel verschlüsseln** nutzt die native Verschlüsselung von ZFS (AES-256-GCM) auf `<pool>/pithyx`, sodass alles, was Pithyx auf dem Pool ablegt, mit einem Schlüssel verschlüsselt ist: freigegebene Ordner, Home-Ordner, Apps und ihre Daten, das Log-Archiv und die Vorschaubilder. Das Wurzel-Dataset des Pools enthält keine Daten und bleibt unverschlüsselt.

### Der Schlüssel

Die Box erzeugt den Schlüssel; einen eigenen kannst du nicht wählen. Der Schritt **Schlüssel** zeigt ihn einmal, als `PXPK-` gefolgt von 56 Zeichen in Vierergruppen. Kopiere ihn, lade ihn mit **Herunterladen** als Textdatei herunter oder **Drucke** ihn aus, bewahre ihn getrennt von der Box auf und bestätige **Ich habe den Schlüssel sicher und getrennt von der Box aufbewahrt.** und gib die zwei Vierergruppen aus deiner Kopie ein, nach denen gefragt wird, bevor es weitergeht. Das Blatt enthält den Schlüssel auch in Hex mit den Befehlen, die den Pool ohne Pithyx öffnen. Die letzten Zeichen prüfen die übrigen, sodass ein Tippfehler als [`storage.encryption.key_invalid`](/de/docs/reference/errors/#storage.encryption.key_invalid) auffällt, bevor der Pool gefragt wird; beim Eingeben spielen Bindestriche, Leerzeichen und Groß- oder Kleinschreibung keine Rolle.

:::danger[Ohne den Schlüssel sind die Daten verloren]
Pithyx hat keinen Weg an einem verlorenen Schlüssel vorbei. Bewahre den Schlüssel auch dann auf, wenn der Schlüsseltresor ihn hat: Der Tresor liegt auf der Systemplatte, die eine Neuinstallation ersetzt, und er ist nie Teil einer [Konfigurationssicherung](/de/docs/admin/backup/).
:::

### Entsperren

Der Assistent fragt, wie der Pool entsperrt wird, und **Automatisch bei jedem Start, aus dem Schlüsseltresor** ist vorausgewählt:

- **Automatisch bei jedem Start, aus dem Schlüsseltresor**: Die Box bewahrt den Schlüssel in einem Tresor auf ihrer Systemplatte auf (`/var/lib/pithyx/pool-keys/`, eine Datei pro Pool, nur für root lesbar) und entsperrt den Pool bei jedem Start und jedem Import. Das schützt die Daten auf Platten, die gestohlen oder weggeworfen werden, nicht auf einer gestohlenen Box: Wer die Box hat, hat auch den Tresor.
- **Von Hand nach jedem Start**: Der Pool bleibt nach jedem Start gesperrt, bis ein Administrator seinen Schlüssel eingibt. Das schützt die Daten auch, wenn die ganze Box gestohlen wird.

Ein gesperrter Pool zeigt **Gesperrt** und **Entsperren…**. Entsperren lädt den Schlüssel und hängt die Datasets so ein wie ein Import (nichts, was einen Systemordner verdecken würde). Seine freigegebenen Ordner, Home-Ordner und Apps sind nicht verfügbar, solange er gesperrt ist: Dateien und die Dateidienste sagen das, und Apps auf dem Pool starten nicht. Mit **Schlüssel im Schlüsseltresor aufbewahren und den Pool bei jedem Start automatisch entsperren** kommt der eingegebene Schlüssel in den Tresor, sobald er den Pool geöffnet hat. Öffnet der Schlüssel aus dem Tresor einen Pool nicht (er wurde anderswo geändert), bleibt der Pool gesperrt, und die Karte sagt das; gib dann den Schlüssel ein. Ein gültiger Schlüssel eines anderen Pools wird mit [`storage.encryption.key_wrong`](/de/docs/reference/errors/#storage.encryption.key_wrong) abgelehnt.

Ein verschlüsselter Pool von einer anderen Box oder nach einer Neuinstallation wird wie jeder andere Pool importiert; er bleibt gesperrt, bis du ihn mit seinem Schlüssel entsperrst.

### Schlüssel ändern und der Tresor

**Schlüssel ändern…** auf einem entsperrten Pool erzeugt einen neuen Schlüssel, zeigt ihn wie der Assistent und ändert den Schlüssel des Pools erst, wenn du bestätigt hast, dass du ihn aufbewahrt hast; der alte Schlüssel funktioniert sofort nicht mehr. Die Daten werden nicht neu geschrieben. Der Dialog entscheidet auch, ob der neue Schlüssel in den Tresor kommt.

**Schlüssel aus dem Tresor entfernen…** löscht den Schlüssel des Pools aus dem Tresor. Der Pool bleibt entsperrt, bis die Box neu startet, und wartet dann auf seinen Schlüssel. Wird ein Pool zerstört, löscht das auch seinen Schlüssel aus dem Tresor. Um einen Schlüssel in den Tresor zu legen, wähle **Entsperren…**, gib den Schlüssel ein und hake die Tresor-Option an: Der Schlüssel wird zuerst am Pool geprüft, auch wenn der Pool schon entsperrt ist. Auch das Ändern des Schlüssels legt den neuen in den Tresor.

**Schlüssel anzeigen…** zeigt den Schlüssel aus dem Tresor erneut, nach deinem Passwort und, wenn die Zwei-Faktor-Anmeldung an ist, einem Authenticator-Code; nutze das, wenn deine Kopie verloren ist.

### USB-Stick

**Schlüssel auf USB-Stick schreiben…** auf der Karte eines verschlüsselten Pools schreibt seinen Schlüssel auf einen USB-Stick, eine Speicherkarte oder eine andere Wechselplatte mit FAT-, exFAT- oder ext-Dateisystem: eine Datei `pithyx-keys/<Pool-GUID>.key`, die den Schlüssel in Hex-Form enthält, neben allem anderen auf dem Stick. Pithyx formatiert nie einen Stick und bietet nur Sticks an, die nichts anderes benutzt (keiner, der anderweitig eingehängt ist, keiner, der zu einem Pool gehört). Der Schlüssel ist der, den du eingibst, oder der aus dem Tresor nach deinem Passwort und, wenn die Zwei-Faktor-Anmeldung an ist, einem Authenticator-Code. Beide werden am Pool geprüft, bevor sie geschrieben werden, und die Datei wird zurückgelesen, bevor der Dialog meldet, dass es fertig ist. **Schlüssel danach aus dem Schlüsseltresor entfernen** ist angehakt: Mit dem Schlüssel im Tresor würde die Box den Pool auch ohne den Stick entsperren.

Sobald ein Stick mit dem Schlüssel steckt, beim Start oder später, entsperrt die Box den Pool, der auf ihn wartet; sie hängt den Stick nur schreibgeschützt ein, solange sie die Datei liest. Danach kann der Stick abgezogen werden: **Der Pool bleibt entsperrt, bis die Box neu startet**, und die Karte und eine Benachrichtigung sagen das. Nach einem Neustart wartet der Pool wieder auf den Stick. Jeder Stick mit der Datei funktioniert, ein zweiter Stick kann also Ersatz sein.

:::caution[Wer den Stick und die Platten hat, hat die Daten]
Wie der Tresor schützt ein Stick die Daten auf gestohlenen Platten; die Daten auf einer gestohlenen Box schützt er nur, solange der Stick nicht bei der Box ist. Bewahre ihn getrennt von der Box auf und behalte auch deine Kopie des Schlüssels: Ein Stick kann kaputtgehen.
:::

Ein Stick, dessen Schlüssel den Pool nicht öffnet (ein älterer Schlüssel nach **Schlüssel ändern…** oder eine beschädigte Datei), lässt den Pool gesperrt und unberührt, mit einer kritischen Speicherwarnung: Entsperre ihn mit dem Schlüssel und schreibe den Schlüssel danach erneut auf den Stick. **Schlüssel ändern…** warnt, wenn ein Stick den aktuellen Schlüssel enthält.

Schlüssel gehen über die Standardeingabe an ZFS, nie als Befehlsargument, und erscheinen nie in einem Log, einem Plan oder auf dem Pool selbst. Administratoren bekommen eine Speicherwarnung, solange ein Pool auf seinen Schlüssel oder seinen Stick wartet. Was zu tun ist, wenn etwas schiefgeht, auch wie sich ein Pool ohne Pithyx öffnen lässt: [Verschlüsselte Pools: wenn etwas schiefgeht](/de/docs/admin/encryption-emergencies/).

## Zustand und Warnungen

Administratoren bekommen eine Desktop-Benachrichtigung, wenn eine Speicherwarnung auftritt oder sich verschlimmert, und eine zweite, die mit „Behoben:“ beginnt und das Problem nennt, wenn es verschwindet. Die Warnungen betreffen: Pool angehalten, beeinträchtigt, ausgefallen oder offline, eine fehlende Platte, Datenfehler, Scrub-Fehler, einen zu 80 Prozent gefüllten Pool (Warnung) oder zu 90 Prozent gefüllten Pool (kritisch) und ausfallende oder bedenkliche SMART-Werte. Du kannst eine Warnung für dich stummschalten.

- **Jetzt prüfen** (Scrub), anhalten, fortsetzen und stoppen. Ein Zeitplan pro Pool: Aus, Wöchentlich oder Monatlich (Standard Monatlich).
- SMART wird mit `smartctl -n standby` gelesen (30 Sekunden Zeitlimit), sodass schlafende Platten weiterschlafen. Zustände: Gesund, Warnung, Fällt aus, Kein SMART, Unbekannt. Kurze Selbsttests laufen standardmäßig wöchentlich und lange monatlich; eine schlafende Platte wird übersprungen und später erneut versucht, nie geweckt.
