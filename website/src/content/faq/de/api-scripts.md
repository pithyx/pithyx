---
question: 'Kann ich die API aus einem Skript nutzen?'
category: development
tags: [api, rest, skript, automatisierung, token, curl, openapi]
order: 50
status: draft
---

Ja, aber persönliche Zugriffstokens gibt es nicht: Ein Skript meldet sich wie der Desktop mit `POST /api/auth/login` an, behält das Cookie `pithyx_session` und schickt bei jeder Anfrage außer `GET` den Header `X-Pithyx-Request: 1` mit, auch beim Login. Der Daemon liefert sein OpenAPI-Dokument unter `/api/openapi.json` aus.

Details: [API](/de/docs/developers/daemon/api/).
