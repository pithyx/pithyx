---
title: 'License'
description: 'AGPL-3.0 for the system with an attribution term for forks, MIT for the npm packages, and the third-party notices.'
status: ready
sidebar:
  order: 1
---

The Pithyx system (daemon, desktop, installer, image tooling and this website) is licensed under the GNU Affero General Public License version 3. The three npm packages for app developers, `@pithyx/sdk`, `@pithyx/ui` and `@pithyx/cli`, are licensed under the MIT License.

## The system

The `LICENSE` file in the repository is the GNU AGPL v3. The `NOTICE` file adds three terms under section 7 of that license: an attribution (7(b)), marking modified versions (7(c)) and no rights to the name and logo (7(e)).

## Attribution in forks and redistributions

Pithyx was originally created by [Schecher1](https://github.com/Schecher1). Anyone may fork, change and redistribute it under the AGPL. Copies and modified versions keep this attribution:

> Based on Pithyx (https://github.com/pithyx/pithyx), originally created by Schecher1 (https://github.com/Schecher1), licensed under the GNU AGPL-3.0.

- **Source and docs**: the `NOTICE` file, and wherever the attribution appears in the source and the documentation, such as the README and this page.
- **User interface**: the **About Pithyx** app (or an equivalent, reasonably visible place); a faithful translation is allowed, as the desktop shows it in German.

## Name and logo

The license grants no rights to the name "Pithyx" or the Pithyx logo as a trademark. A modified version that is passed on needs a product name of its own and must not use the Pithyx logo; it must not present itself as Pithyx or as a release of the project, and it says that it is based on Pithyx (the attribution above). Passing on unmodified copies of Pithyx under its name is fine. These terms apply to the system only, not to the MIT-licensed npm packages.

## Source code

The source of Pithyx itself is the repository at the release's tag. An image (ISO or update file) also contains Debian packages, many of them under the GPL. Their source packages, in exactly the versions of the image, are attached to each release as `pithyx-<version>-sources-<n>.tar`; `sources.tsv` in the first part says which source package covers which binary package. The ZFS module is built from Debian's `zfs-linux` of the same version, and the boot loader of the ISO from Debian's `grub2`; both are in the archives too. The maintainers collect them with `tools/release/collect-sources.ts` from snapshot.debian.org.

## The npm packages

`sdk/`, `packages/ui` and `packages/cli` each contain their own `LICENSE` file (MIT, copyright "Pithyx contributors") and declare `MIT` in their `package.json`. You can build apps of any license with them. The manifest and Compose schemas under `sdk/schema/` are part of `@pithyx/sdk`. Apps themselves keep the license their authors choose; catalogs require an SPDX license expression in the manifest.

## Third-party notices

Bundled third-party material keeps its own license. The fonts IBM Plex Sans, Sora and JetBrains Mono are under the SIL Open Font License 1.1 and are shipped unmodified. Other bundled components include Tabler Icons, React Aria Components, PDF.js and SkiaSharp. Their notices are listed in the **About Pithyx** app of the desktop, and the license texts are in `desktop/public/licenses/` in the repository.
