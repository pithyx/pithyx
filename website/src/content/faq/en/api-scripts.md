---
question: 'Can I use the API from a script?'
category: development
tags: [api, rest, script, automation, token, curl, openapi]
order: 50
status: ready
---

Yes, but there are no personal access tokens: a script signs in like the desktop with `POST /api/auth/login`, keeps the `pithyx_session` cookie and sends the header `X-Pithyx-Request: 1` on every request that is not a `GET`, the login included. The daemon serves its OpenAPI document at `/api/openapi.json`.

Details: [API](/docs/developers/daemon/api/).
