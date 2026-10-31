---
title: 'pithyx pack'
description: 'pithyx pack validates an app project and writes its deterministic package <id>-<version>.pithyxapp.'
status: ready
audience: developer
sidebar:
  order: 4
---

`pithyx pack` runs the checks of [`pithyx validate`](/docs/reference/cli/validate/) and, when there is no error, writes the app package `<id>-<version>.pithyxapp`: a gzip-compressed tar archive that you install in the Store or publish in a catalog. The same files always give the same bytes.

## Synopsis

<!-- generated:cli-synopsis pack -->

```sh
pithyx pack [project folder] [--frontend <folder>] [--out <path>] [--images] [--allow-reserved-id]
```

<!-- /generated -->

## Options

<!-- generated:cli-options pack -->

| Option                | Value      | Type    | Default     | Description                                                                                                                                                                                                                                                                          |
| --------------------- | ---------- | ------- | ----------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `--frontend`          | `<folder>` | string  | `dist`      | The built frontend                                                                                                                                                                                                                                                                   |
| `--out`               | `<path>`   | string  | the project | The package file, or a folder for it                                                                                                                                                                                                                                                 |
| `--images`            | –          | boolean | off         | Add backend/images/&lt;arch&gt;/&lt;id&gt;.tar for offline installs, saved with the local docker (save, or pull first, per architecture of the lock). Needs Docker with access to the registries; the images make the package large, and their bytes depend on Docker's save format. |
| `--allow-reserved-id` | –          | boolean | off         | Let an id start with org.pithyx., which is reserved for the system apps and the official catalog: the app's id (validate, lock, build, pack, dev; a box still refuses it in a package from a file) or the id of catalog.json (catalog check)                                         |

<!-- /generated -->

`--out` ending in `.pithyxapp` is the package file itself; anything else is a folder, which gets `<id>-<version>.pithyxapp`. A folder that does not exist yet is created, also the folder of a `.pithyxapp` path.

## The package

- `pithyx.json` and `icon.svg` first, then every other path sorted by code unit.
- `frontend/` with every file of the built frontend except hidden ones.
- `README.md`, `README`, `LICENSE`, `LICENSE.md`, `LICENSE.txt` when they exist at the project root.
- With a backend: its Compose file and `backend/images.lock.json`.
- `CHECKSUMS`: the SHA-256 of every other file in the format of `sha256sum`, so `sha256sum -c CHECKSUMS` checks an unpacked package.
- With `--images`: `backend/images/<arch>/<image id>.tar` for each image and architecture of the lock. Each image is saved with `docker save` from your Docker when it has it (images of `pithyx build`): by its image id with the classic image store, by its manifest digest with the containerd image store (the default since Docker 29), with `--platform` where Docker knows it. Any other image is first pulled with `docker pull --platform` by its digest; an image of `pithyx build` (`pithyx.local/...`) is never pulled. The saved archive must hold the image config of the lock. When nothing works, the error lists every attempt. The archives make the package large, and their bytes depend on Docker's save format.

Fixed modes, owners and times and no time in the gzip header make the archive deterministic.

## Files

| File                                                       | Access                                                     |
| ---------------------------------------------------------- | ---------------------------------------------------------- |
| everything `validate` reads                                | read                                                       |
| the package (default `<project>/<id>-<version>.pithyxapp`) | written, replaced if it exists                             |
| a temporary folder `pithyx-images-*`                       | holds the saved images with `--images`, removed afterwards |

## Exit codes

| Code | When                                                                                                                                                   |
| ---- | ------------------------------------------------------------------------------------------------------------------------------------------------------ |
| 0    | Packed: `Packed <path> (<n> bytes).`                                                                                                                   |
| 1    | A finding of level error (`Not packed: fix the errors above first.`), no valid `pithyx.json`, or Docker fails with `--images`.                         |
| 2    | `--out` is neither a `.pithyxapp` file nor a folder, `--images` for an app without a backend or without a valid lock, or more than one project folder. |

## Findings

The same as [`pithyx validate`](/docs/reference/cli/validate/#findings); see [Findings](/docs/reference/cli/findings/).

## Examples

```sh
pithyx pack                          # ./com.example.notes-1.0.0.pithyxapp
pithyx pack --out dist-packages/     # into a folder, created if needed
pithyx pack --out notes.pithyxapp    # a file name of your own
pithyx pack --images                 # with the backend images for offline installs
```
