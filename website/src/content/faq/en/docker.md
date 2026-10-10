---
question: 'Do I have to set up Docker, and can I run my own containers?'
category: apps
tags: [docker, compose, containers, docker-compose, docker.sock, self-hosting]
order: 60
status: ready
---

No setup is needed: Pithyx runs Debian's Docker itself, and only while an app with a backend is installed. Containers run only as part of an app, from a checked Compose subset with images pinned by digest, on an internal network without host ports. Pithyx has no general container manager, and version 0.1 refuses the Docker socket and host-wide rights.

Details: [Backends and Docker](/docs/admin/apps-and-store/#backends-and-docker).
