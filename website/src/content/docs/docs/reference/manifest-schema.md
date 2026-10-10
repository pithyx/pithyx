---
title: 'Manifest schema'
description: 'The JSON Schema of pithyx.json ships with @pithyx/sdk and is the source for the manifest rules.'
status: draft
audience: developer
sidebar:
  order: 3
---

`pithyx.json` is validated against one JSON Schema (draft 2020-12). This page says where it lives; the fields are explained in [The manifest](/docs/developers/apps/manifest/).

## Fields

The schema is `sdk/schema/manifest.v1.schema.json` in the repository, published in the npm package as `@pithyx/sdk/schema/manifest.v1.schema.json` and identified as `https://pithyx.org/schema/manifest.v1.schema.json`. The box, `pithyx validate` and the generated TypeScript types in the SDK all read this file. Unknown fields are errors.

Related files in the same folder: `compose.v1.rules.json` (the Compose allowlist), `images-lock.v1.schema.json`, and the catalog formats `catalog.v1.schema.json`, `index.v1.schema.json`, `index-signature.v1.schema.json`, `maintainers.v1.schema.json` and `catalog.v1.rules.json`.
