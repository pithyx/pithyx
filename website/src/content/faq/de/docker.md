---
question: 'Muss ich Docker einrichten, und kann ich eigene Container betreiben?'
category: apps
tags: [docker, compose, container, docker-compose, docker.sock, selbst hosten]
order: 60
status: ready
---

Einrichten musst du nichts: Pithyx betreibt das Docker von Debian selbst, und nur solange eine App mit Backend installiert ist. Container laufen nur als Teil einer App, aus einer geprüften Teilmenge von Compose mit per Digest festgelegten Images, in einem internen Netzwerk ohne Host-Ports. Eine allgemeine Container-Verwaltung hat Pithyx nicht, und Version 0.1 lehnt den Docker-Socket und Rechte auf dem ganzen Host ab.

Details: [Apps und der Store](/de/docs/admin/apps-and-store/).
