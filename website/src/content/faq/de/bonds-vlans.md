---
question: 'Kann ich mehrere Netzwerkkarten, Bonding oder VLANs nutzen?'
category: network
tags: [bond, bonding, lacp, link aggregation, vlan, 802.1q, zwei netzwerkkarten, ausfallsicherheit]
order: 20
status: ready
---

Ja. In Einstellungen > **Netzwerk** bekommt jede Karte eigene Einstellungen, **Bond hinzufügen** bündelt Karten (Active-Backup, LACP oder adaptive Lastverteilung) und **VLAN hinzufügen** legt ein getaggtes VLAN auf einer Karte oder einem Bond an. IPv4 und IPv6 können jeweils automatisch, statisch oder aus sein. Für WLAN und Bridges gibt es keine Einstellungen.

Details: [Netzwerk](/de/docs/admin/network/).
