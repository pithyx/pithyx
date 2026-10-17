---
title: '@pithyx/cli'
description: 'The pithyx command: validate, lock, build, pack, dev and the catalog commands.'
status: ready
audience: developer
sidebar:
  order: 4
---

The `pithyx` command checks, develops and packages Pithyx apps and runs the CI of catalog repositories. It has six commands: `validate`, `lock`, `build`, `pack`, `dev` and `catalog`. The commands use the same validators as the box, so an error you see locally is the error the box would give.

```sh
npm install --save-dev @pithyx/cli@next
npx pithyx --help
```

Usage: `pithyx <command> [project folder] [options]`. The folder defaults to the current directory. Exit codes: 0 for success, 1 for findings or a failure, 2 for wrong usage (an unknown command, a missing option). `pithyx --version` prints the version. Node.js 22.12 or newer is required.

Every command with all its options, exit codes, files and findings is in the [pithyx command reference](/docs/reference/cli/).

The option `--frontend <folder>` names the built frontend (default `dist`) for `validate`, `lock`, `pack` and `dev`.

## pithyx dev

```sh
pithyx dev [folder] [--port 7400] [--host 127.0.0.1] [--endpoint name=url ...] [--no-docker]
```

Runs the mock desktop with your built frontend in the box's sandbox and a proxy to the backend. The port must be an integer from 1 to 65535. A backend runs with `docker compose` from `backend/compose.dev.yaml` if it exists, otherwise from the pinned `backend/compose.yaml`. `--endpoint name=url` (repeatable) points an endpoint at a server you run yourself, and `--no-docker` leaves the backend alone (endpoints without `--endpoint` then answer 503). It needs a valid `pithyx.json`. Stop with Ctrl+C. Details: [Your first app](/docs/developers/apps/first-app/#pithyx-dev).

With Vite, use the plugin instead: `import pithyx from '@pithyx/cli/vite'` and add `pithyx()` to `plugins`, then run `vite`. Options: `project`, `validate` (default true; validates after `vite build`), `endpoints` and `docker`.

## pithyx build

```sh
pithyx build [folder] [--oci-out <dir>] [--pin <prefix>]
```

Builds the services of `backend/compose.dev.yaml` that have `build:`. It needs an app with a backend and that file. By default it uses the local Docker: it builds for this machine's architecture (amd64 or arm64), tags the images `pithyx.local/<id>/<service>:<version>`, saves them and writes the pinned `backend/compose.yaml` and `backend/images.lock.json`. Services that only name an `image` are resolved from their registry.

- `--oci-out <dir>` builds reproducibly for amd64 and arm64 with `docker buildx` into OCI archives `<dir>/<service>.tar` and pushes nothing. It needs `SOURCE_DATE_EPOCH` in the environment or a Git checkout.
- `--pin <prefix>` (only together with `--oci-out`) pins the Compose file to `<prefix>/<id>/<service>:<version>@<digest>` as the images will be after you push the archives.

## Other app commands

- `pithyx validate [folder]` checks `pithyx.json`, `icon.svg`, the entry in the built frontend, the package layout, `backend/compose.yaml` and `backend/images.lock.json`. It warns while `icon.svg` is the template icon. On success it prints `<id> <version> is valid.`
- `pithyx lock [folder]` resolves the images of `backend/compose.yaml` and writes `backend/images.lock.json`. It needs the network.
- `pithyx pack [folder] [--out <file or folder>] [--images]` validates, then writes `<id>-<version>.pithyxapp`. `--out` is a `.pithyxapp` file or a folder, created if needed. `--images` adds the images for offline installs and needs Docker.

## pithyx catalog

Run these in the root of a catalog repository. The first argument is the subcommand. `--catalog <file>` names the catalog file (default `catalog.json`).

| Subcommand                                                                                                                                              | Purpose                                                                                                                                            |
| ------------------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------- |
| `catalog check --base <commit> --author <login> [--index <file>]`                                                                                       | Checks the pull request rules on the changes since the base commit.                                                                                |
| `catalog pending [--index <file>]`                                                                                                                      | Prints, as JSON, the app versions in `apps/` that `catalog-next` does not list yet.                                                                |
| `catalog index <branch folder> --channel stable\|beta (--add <package>... --source <checkout> --repository <url> --commit <sha> \| --promote <folder>)` | Adds built packages, or the versions of another branch, and writes `index.json` with the next sequence number.                                     |
| `catalog sign <branch folder> --key-id <id> [--append]`                                                                                                 | Signs `index.json` with the Ed25519 private key (PEM) in the environment variable `PITHYX_SIGNING_KEY`. `--append` keeps signatures by other keys. |
| `catalog verify <branch folder> --channel stable\|beta [--key <id>=<base64>]...`                                                                        | Checks the index, every file and, with keys, the signature, as a box does.                                                                         |

The signing key is never accepted as a flag. See [Publishing to a catalog](/docs/developers/apps/publishing/).

## JavaScript API

`@pithyx/cli` also exports the validators for use in scripts, among others: `validateManifest`, `validateCompose`, `validateIcon`, `checkLayout`, `isSafePath`, `pack`, `packageName`, `readProject`, `resolveLock`, `formatLock`, `isValidCron` and `describe`, and the constants `MANIFEST_FILE`, `ICON_FILE`, `LOCK_FILE`, `CHECKSUMS_FILE`, `ARCHITECTURES`, `SUPPORTED_MANIFEST_VERSION` and `RESERVED_ID_PREFIX`.
