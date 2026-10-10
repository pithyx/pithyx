---
question: 'Wo speichern Apps ihre Daten?'
category: apps
tags: [app-daten, app-pool, dataset, docker-daten, deinstallieren, systemplatte]
order: 40
status: ready
---

Auf dem App-Pool, den ein Administrator bei der ersten App-Installation wählt, in einem Dataset pro App; auch die Daten von Docker liegen auf diesem Pool, nie auf der Systemplatte. Solange eine App installiert ist, lässt sich der Pool nicht wechseln. Beim Deinstallieren fragt der Store, ob die Daten bleiben sollen, damit eine spätere Installation sie wiederfindet.

Details: [Apps und der Store](/de/docs/admin/apps-and-store/).
