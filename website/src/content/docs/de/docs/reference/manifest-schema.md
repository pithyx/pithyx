---
title: 'Manifest-Schema'
description: 'Das JSON-Schema von pithyx.json wird mit @pithyx/sdk ausgeliefert und ist die Quelle der Manifest-Regeln.'
status: draft
audience: developer
sidebar:
  order: 3
---

`pithyx.json` wird gegen ein einziges JSON-Schema (Draft 2020-12) geprüft. Diese Seite sagt, wo es liegt; die Felder werden unter [Das Manifest](/de/docs/developers/apps/manifest/) erklärt.

## Felder

Das Schema ist `sdk/schema/manifest.v1.schema.json` im Repository, im npm-Paket als `@pithyx/sdk/schema/manifest.v1.schema.json` veröffentlicht und mit `https://pithyx.org/schema/manifest.v1.schema.json` bezeichnet. Die Box, `pithyx validate` und die erzeugten TypeScript-Typen im SDK lesen alle diese Datei. Unbekannte Felder sind Fehler.

Verwandte Dateien im selben Ordner: `compose.v1.rules.json` (die Compose-Allowlist), `images-lock.v1.schema.json` und die Katalogformate `catalog.v1.schema.json`, `index.v1.schema.json`, `index-signature.v1.schema.json`, `maintainers.v1.schema.json` und `catalog.v1.rules.json`.
