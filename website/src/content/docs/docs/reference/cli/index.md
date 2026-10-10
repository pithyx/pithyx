---
title: 'pithyx command'
description: 'Reference of the pithyx command of @pithyx/cli: usage, commands, global options, exit codes, environment variables and output.'
status: ready
audience: developer
sidebar:
  order: 0
---

`pithyx` is the command of the npm package `@pithyx/cli` (MIT). It validates, locks, builds, packs and runs Pithyx apps on your machine, and it runs the CI of catalog repositories. It checks with the same rules as the box, so an error you see locally is the error the box would report. The synopses and option tables on these pages are generated from the CLI's own help text and argument parser each time the site is built; the text around them is written by hand. For an introduction with installation see [@pithyx/cli](/docs/sdk/cli/).

## Usage

<!-- generated:cli-usage -->

```sh
pithyx <command> [project folder] [options]
```

<!-- /generated -->

The project folder is the first argument after the command and defaults to the current directory. A second one fails with exit code 2 (`pithyx validate takes one project folder, not: ...`). The `catalog` subcommands run in the root of a catalog repository and take a branch folder instead.

Every command accepts every option the parser knows, but only the options listed on a command's page change what it does; the others are ignored. An option the parser does not know fails with exit code 1 (`pithyx: Unknown option '--x'`). Node.js 22.12 or newer is required.

## Commands

<!-- generated:cli-commands -->

| Command                                                          | Description                                                                                                                                                                                                                                                                                                                                                                                                                    |
| ---------------------------------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| [`pithyx validate`](/docs/reference/cli/validate/)               | Check the project as the box checks its package: pithyx.json, icon.svg, the entry in the built frontend, the package layout, backend/compose.yaml and backend/images.lock.json. Warns while icon.svg is still the template icon.                                                                                                                                                                                               |
| [`pithyx lock`](/docs/reference/cli/lock/)                       | Resolve the images of backend/compose.yaml from their registries and write backend/images.lock.json (the platform manifest and image id for amd64 and arm64). Needs the network; registries are asked anonymously or with the credentials stored in ~/.docker/config.json (not with credential helpers).                                                                                                                       |
| [`pithyx build`](/docs/reference/cli/build/)                     | Build the services of backend/compose.dev.yaml that have build: with the local Docker for this machine, tag them pithyx.local/&lt;id&gt;/&lt;service&gt;:&lt;version&gt; and pin backend/compose.yaml and backend/images.lock.json to them (for pack --images). --oci-out builds reproducibly for amd64 and arm64 with buildx into OCI archives and pushes nothing; --pin then pins to those archives as pushed to a registry. |
| [`pithyx pack`](/docs/reference/cli/pack/)                       | Validate, then write &lt;id&gt;-&lt;version&gt;.pithyxapp: a deterministic tar.gz with pithyx.json, icon.svg, frontend/, the backend's compose.yaml and lock, README and LICENSE when they exist, and CHECKSUMS. The same files give the same bytes.                                                                                                                                                                           |
| [`pithyx dev`](/docs/reference/cli/dev/)                         | Run the mock desktop with the app in the box's sandbox and a proxy to the backend, which runs with docker compose (backend/compose.dev.yaml instead of the pinned file when it exists). With Vite, use the plugin @pithyx/cli/vite and run vite instead.                                                                                                                                                                       |
| [`pithyx catalog check`](/docs/reference/cli/catalog-check/)     | The pull request rules on the changes since the base commit                                                                                                                                                                                                                                                                                                                                                                    |
| [`pithyx catalog pending`](/docs/reference/cli/catalog-pending/) | The app versions of apps/ that catalog-next does not list yet, as JSON                                                                                                                                                                                                                                                                                                                                                         |
| [`pithyx catalog index`](/docs/reference/cli/catalog-index/)     | Adds built packages or the versions of another branch, writes index.json                                                                                                                                                                                                                                                                                                                                                       |
| [`pithyx catalog sign`](/docs/reference/cli/catalog-sign/)       | Signs index.json with the Ed25519 key (PEM) in $PITHYX_SIGNING_KEY                                                                                                                                                                                                                                                                                                                                                             |
| [`pithyx catalog verify`](/docs/reference/cli/catalog-verify/)   | Checks the signature (with keys), the index and every file as a box does                                                                                                                                                                                                                                                                                                                                                       |

<!-- /generated -->

`pithyx help` prints the same help as `pithyx --help`.

## Global options

<!-- generated:cli-global-options -->

| Option            | Description      |
| ----------------- | ---------------- |
| `-h`, `--help`    | Show this help   |
| `-v`, `--version` | Show the version |

<!-- /generated -->

`--version` comes first: `pithyx validate --version` only prints the version. `pithyx` without a command prints the help and exits with 2.

## Exit codes

| Code | Meaning                                                                                                                                                                                                                                                                          |
| ---- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 0    | Success. Also `--help`, `--version` and `pithyx help`. Warnings alone do not change the code.                                                                                                                                                                                    |
| 1    | A finding of level error, or a step failed: an invalid file, a Docker or registry call, an unknown option, an option value the command cannot use (for example `--channel` other than `stable` or `beta`, `--endpoint` without `=`). Failures print `pithyx: <message>`.         |
| 2    | Wrong usage that the CLI checks itself: no command, an unknown command or catalog subcommand, more than one project folder, a missing required option, `--out` that is neither a `.pithyxapp` file nor a folder, `--port` that is not a port, `--images` without an images lock. |

`pithyx dev` keeps running; it exits with 0 after Ctrl+C or SIGTERM, once its backend is stopped.

## Output

Results go to standard output (`<id> <version> is valid.`, `Packed ...`, the JSON of `catalog pending`). Findings go to standard error, one per line, as `<level> <code>: <message>`, for example:

```text
error app.manifest.schema_violation: The app's manifest is not valid at /window/minSize (required).
warning cli.icon.template: icon.svg is still the template icon (the Pithyx box). Replace it with the icon of your app.
```

The message is the box's English text for its codes, so the CLI says what the box would say. Every code is listed under [Findings](/docs/reference/cli/findings/).

## Environment

| Variable or tool     | Used by                                                                                                                                                                                                            |
| -------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `PITHYX_SIGNING_KEY` | [`catalog sign`](/docs/reference/cli/catalog-sign/): the Ed25519 private key in PEM. It is never accepted as an option.                                                                                            |
| `SOURCE_DATE_EPOCH`  | [`build --oci-out`](/docs/reference/cli/build/): the time of every file in the images. Without it the time of the last Git commit of the project is used.                                                          |
| `DOCKER_CONFIG`      | [`lock`](/docs/reference/cli/lock/) and [`build`](/docs/reference/cli/build/): the folder of `config.json` with registry credentials (default `~/.docker`). Only `auths` entries are read, not credential helpers. |
| `docker`             | [`build`](/docs/reference/cli/build/), [`pack --images`](/docs/reference/cli/pack/) and [`dev`](/docs/reference/cli/dev/); `build --oci-out` needs `docker buildx`.                                                |
| `git`                | [`catalog check`](/docs/reference/cli/catalog-check/) and `build --oci-out` without `SOURCE_DATE_EPOCH`.                                                                                                           |

The CLI sends nothing anywhere on its own. Only [`lock`](/docs/reference/cli/lock/) and [`build`](/docs/reference/cli/build/) contact registries, to resolve image digests.

## Vite

With Vite you do not run `pithyx dev`: add the plugin from `@pithyx/cli/vite` and run `vite`. See [@pithyx/cli](/docs/sdk/cli/).
