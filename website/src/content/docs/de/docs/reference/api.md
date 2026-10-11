---
title: 'REST-API-Referenz'
description: 'Die REST-API des Daemons wird durch ein OpenAPI-Dokument beschrieben, das mit jedem Build ausgeliefert wird.'
status: ready
audience: developer
sidebar:
  order: 2
---

Die REST-API des Daemons wird durch ein einziges OpenAPI-3-Dokument beschrieben, das aus dem Code erzeugt wird. Diese Seite sagt, wo du es findest; sie listet die Endpunkte nicht von Hand auf.

## Endpunkte

Die Quelle ist `packages/api-client/openapi.json` im Repository (Titel „Pithyx API“, Version `v1`). Ein laufender Daemon liefert dasselbe Dokument ohne Anmeldung unter `/api/openapi.json` aus. Die Operationen sind nach Tags gruppiert, und die Schemas enthalten die WebSocket-Nachrichten und die Liste der Fehlercodes.

Das Dokument beschreibt nicht den WebSocket unter `/ws`, WebDAV unter `/webdav/` oder die App-Frontends und den Proxy unter `/apps/<id>/`. Das Dokument enthält kein Sicherheitsschema: Jeder Endpunkt, der nicht als anonym markiert ist, braucht das Sitzungs-Cookie, und jeder Schreibzugriff braucht den Header `X-Pithyx-Request: 1`. Die Konventionen, das WebSocket-Protokoll und die Liste der Topics stehen unter [API](/de/docs/developers/daemon/api/). Fehlercodes stehen in der [Fehlerreferenz](/de/docs/reference/errors/).

## Schemas

Alle Request- und Response-Schemas sind Komponenten desselben Dokuments. Die TypeScript-Typen in `@pithyx/api-client` werden daraus erzeugt.
