---
title: 'License'
description: 'AGPL-3.0 for the system with an attribution term for forks, MIT for the npm packages, and the third-party notices.'
status: ready
sidebar:
  order: 1
---

The Pithyx system (daemon, desktop, installer, image tooling and this website) is licensed under the GNU Affero General Public License version 3. The three npm packages for app developers, `@pithyx/sdk`, `@pithyx/ui` and `@pithyx/cli`, are licensed under the MIT License.

## The system

The `LICENSE` file in the repository is the GNU AGPL v3. The `NOTICE` file adds terms under section 7(b) of that license, which allows "requiring preservation of specified reasonable legal notices or author attributions". The `NOTICE` file has not been reviewed by a lawyer yet.

## Attribution in forks and redistributions

Pithyx was originally created by [Schecher1](https://github.com/Schecher1). Anyone may fork, change and redistribute it under the AGPL, but a fork or redistribution must not hide the name Pithyx or that it comes from Schecher1. It keeps this attribution:

> Based on Pithyx (https://github.com/pithyx/pithyx), originally created by Schecher1 (https://github.com/Schecher1), licensed under the GNU AGPL-3.0.

- **Source**: the `NOTICE` file with the attribution and its terms, and the README.
- **User interface**: the **About Pithyx** app (or an equivalent, reasonably visible place); a faithful translation is allowed, as the desktop shows it in German.
- **Docs**: wherever the derived work describes its license, origin or authors.

A fork may use its own product name, logo and branding, but where it names itself in these places it states that it is based on Pithyx by Schecher1, and it marks itself as modified (section 5(a) of the AGPL). These terms apply to the system only, not to the MIT-licensed npm packages.

## Source code

The source of Pithyx itself is the repository at the release's tag. An image (ISO or update file) also contains Debian packages, many of them under the GPL. Their source packages, in exactly the versions of the image, are attached to each release as `pithyx-<version>-sources-<n>.tar`; `sources.tsv` in the first part says which source package covers which binary package. The ZFS module is built from Debian's `zfs-linux` of the same version, and the boot loader of the ISO from Debian's `grub2`; both are in the archives too. The maintainers collect them with `tools/release/collect-sources.ts` from snapshot.debian.org.

## The npm packages

`sdk/`, `packages/ui` and `packages/cli` each contain their own `LICENSE` file (MIT, copyright "Pithyx contributors") and declare `MIT` in their `package.json`. You can build apps of any license with them. The manifest and Compose schemas under `sdk/schema/` are part of `@pithyx/sdk`. Apps themselves keep the license their authors choose; catalogs require an SPDX license expression in the manifest.

## Third-party notices

Bundled third-party material keeps its own license. The fonts IBM Plex Sans, Sora and JetBrains Mono are under the SIL Open Font License 1.1 and are shipped unmodified. Other bundled components include Tabler Icons, React Aria Components, PDF.js and SkiaSharp. Their notices are listed in the **About Pithyx** app of the desktop, and the license texts are in `desktop/public/licenses/` in the repository.
