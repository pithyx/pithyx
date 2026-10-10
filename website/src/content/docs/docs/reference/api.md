---
title: 'REST API reference'
description: 'The REST API of the daemon is described by an OpenAPI document that ships with every build.'
status: draft
audience: developer
sidebar:
  order: 2
---

The REST API of the daemon is described by one OpenAPI 3 document, which is generated from the code. This page says where to find it; it does not list the endpoints by hand.

## Endpoints

The source is `packages/api-client/openapi.json` in the repository (title "Pithyx API", version `v1`). A running daemon serves the same document, without signing in, at `/api/openapi.json`. Operations are grouped by tag, and schemas include the WebSocket messages and the error code list.

The document does not describe the WebSocket at `/ws`, WebDAV at `/webdav/`, or the app frontends and proxy under `/apps/<id>/`. There is no security scheme in the document: every endpoint that is not marked anonymous needs the session cookie, and every write needs the header `X-Pithyx-Request: 1`. The conventions, the WebSocket protocol and the topic list are on [API](/docs/developers/daemon/api/). Error codes are in the [error reference](/docs/reference/errors/).

## Schemas

All request and response schemas are components of the same document. The TypeScript types in `@pithyx/api-client` are generated from it.
