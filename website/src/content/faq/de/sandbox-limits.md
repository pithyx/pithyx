---
question: 'Warum funktionieren localStorage oder ein Skript von einem CDN in meiner App nicht?'
category: development
tags: [localstorage, indexeddb, cdn, csp, content security policy, sandbox, iframe, inline-skript]
order: 20
status: ready
---

Apps laufen in einem Sandbox-iframe ohne `allow-same-origin`, deshalb bringen `localStorage` und IndexedDB nichts: Speicher Daten mit `app.storage` aus dem SDK. Die Content Security Policy erlaubt Skripte nur aus der App selbst, also bündel jedes Skript und verzichte auf Inline-`<script>`; Inline-Styles sind erlaubt. `pithyx dev` liefert dieselbe Policy aus, du siehst eine blockierte Anfrage also schon beim Entwickeln.

Details: [Apps im Überblick](/de/docs/developers/apps/overview/).
