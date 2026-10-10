---
question: 'Can I use several network cards, bonding or VLANs?'
category: network
tags: [bond, bonding, lacp, link aggregation, vlan, 802.1q, two network cards, failover]
order: 20
status: ready
---

Yes. In Settings > **Network** each card gets its own settings, **Add bond** combines cards (active-backup, LACP or adaptive load balancing), and **Add VLAN** adds a tagged VLAN on a card or bond. IPv4 and IPv6 can each be automatic, static or off. Wi-Fi and bridges have no settings.

Details: [Network](/docs/admin/network/).
