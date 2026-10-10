---
question: 'Why does localStorage or a script from a CDN not work in my app?'
category: development
tags: [localstorage, indexeddb, cdn, csp, content security policy, sandbox, iframe, inline script]
order: 20
status: ready
---

Apps run in a sandboxed iframe without `allow-same-origin`, so `localStorage` and IndexedDB are of no use: store data with `app.storage` from the SDK. The Content Security Policy allows scripts only from the app itself, so bundle every script and skip inline `<script>`; inline styles are fine. `pithyx dev` serves the same policy, so you see a blocked request while developing.

Details: [The sandbox](/docs/developers/apps/overview/#the-sandbox).
