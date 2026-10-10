---
title: 'License'
description: 'AGPL-3.0 for the system, MIT for the npm packages, and the third-party notices.'
status: ready
sidebar:
  order: 1
---

The Pithyx system (daemon, desktop, installer, image tooling and this website) is licensed under the GNU Affero General Public License version 3. The three npm packages for app developers, `@pithyx/sdk`, `@pithyx/ui` and `@pithyx/cli`, are licensed under the MIT License.

## The system

The `LICENSE` file in the repository is the GNU AGPL v3. The `NOTICE` file adds terms under section 7 of that license: the user interface must keep the attribution text in the About app ("Based on Pithyx (https://github.com/pithyx/pithyx), created by the Pithyx contributors and licensed under the GNU AGPL-3.0."), and modified versions must be marked as modified. The `NOTICE` file has not been reviewed by a lawyer yet.

## The npm packages

`sdk/`, `packages/ui` and `packages/cli` each contain their own `LICENSE` file (MIT, copyright "Pithyx contributors") and declare `MIT` in their `package.json`. You can build apps of any license with them. The manifest and Compose schemas under `sdk/schema/` are part of `@pithyx/sdk`. Apps themselves keep the license their authors choose; catalogs require an SPDX license expression in the manifest.

## Third-party notices

Bundled third-party material keeps its own license. The fonts IBM Plex Sans, Sora and JetBrains Mono are under the SIL Open Font License 1.1 and are shipped unmodified. Other bundled components include Tabler Icons, React Aria Components, PDF.js and SkiaSharp. Their notices are listed in the **About Pithyx** app of the desktop, and the license texts are in `desktop/public/licenses/` in the repository.
