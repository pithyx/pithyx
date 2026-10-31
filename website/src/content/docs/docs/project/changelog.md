---
title: 'Changelog'
description: 'What changed in each release.'
status: ready
sidebar:
  order: 5
---

The first release was 1.0.0 (codename Pyxis), then came 1.1.0 (Carina), and the current one is 1.2.0 (codename Vela); all are marked beta until a release is declared stable. A minor or major version gets a new constellation as its codename, a patch release keeps the one of its minor version (D374). The changes and known limitations of each release are in the changelog file in the repository.

## Releases

The changelog is `CHANGELOG.md` in the repository: [CHANGELOG.md on GitHub](https://github.com/pithyx/pithyx/blob/dev/CHANGELOG.md). It follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and [Semantic Versioning](https://semver.org/spec/v2.0.0.html). Every change that users or developers can see adds a line under "Unreleased"; at a release those lines move under the new version.

The product has one version number, tagged `vX.Y.Z` with a codename. The npm packages (`@pithyx/sdk`, `@pithyx/ui`, `@pithyx/cli`) have their own version line, tagged `sdk-vX.Y.Z`; see [SDK overview](/docs/sdk/overview/#versions-and-channels).
