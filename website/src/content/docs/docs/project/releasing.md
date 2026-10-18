---
title: 'Releasing'
description: 'The release tool tools/release/pithyx-release: building the ISO and update files, signing them and publishing the npm packages.'
status: ready
audience: developer
sidebar:
  order: 6
---

`tools/release/pithyx-release` is one command over the release scripts in `tools/release/`. It knows their order, defaults and preconditions: every command first becomes a plan of plain commands, which `--dry-run` prints and which otherwise run one after the other. Without a command, in a terminal, it shows the state of the checkout, asks which command to run and for its options, prints the plan and runs it only after you confirm.

```sh
tools/release/pithyx-release status
tools/release/pithyx-release update --version 1.0.0-beta.2 --sign --dry-run
tools/release/pithyx-release            # the menu
```

## Commands

| Command        | What it does                                                                                                                  |
| -------------- | ----------------------------------------------------------------------------------------------------------------------------- |
| `status`       | The product and npm versions, the checkout, the built packages in `image/out`, ISOs, update files, baselines and the beta key |
| `checklist`    | The release steps of a version (`--version`, default the product version) and which of them this machine shows done           |
| `iso`          | `image/build.sh` for a version: the ISO and its package list                                                                  |
| `update`       | `build-update.sh` against a baseline, and with `--sign` also `sign-update.sh`                                                 |
| `sign`         | `sign-update.sh` for a file, with the beta key unless `--key` and `--key-id` name another                                     |
| `channel`      | `build-channel.sh`: `channel.json` and its signature for a file (`--key`, `--key-id`, `--sequence`, `--url`)                  |
| `publish-beta` | `publish-beta.sh`: the pre-release `beta-<version>` and a new `channel-beta`                                                  |
| `publish-sdk`  | `publish-sdk.sh` for `@pithyx/sdk`, `@pithyx/ui` and `@pithyx/cli`; `--latest` then moves `latest` to the version             |
| `sources`      | `collect-sources.ts --pack`: the GPL sources of an image as release parts                                                     |
| `baseline`     | Copies a built image's package list to `image/baseline/` (commit it)                                                          |
| `templates`    | `bump-templates.ts` for local clones of the app templates                                                                     |
| `key`          | `create-key.sh`: a new Ed25519 signing key                                                                                    |

## What it checks for you

- `update` builds from the packages of the last `image/build.sh` run. When they are of another version it stops and says so; `--build-image` builds the image of the version first.
- The baseline of an update is `--since <file>`, `--full`, or the package list of `--minimum` (default `1.0.0-beta.1`) from `image/baseline/`, else from `image/out/`. Without one it names the command that keeps it.
- The channel is beta for a prerelease version and stable otherwise. A beta file is signed with `~/.config/pithyx-dev/pithyx-beta-1.pem`; a stable file needs `--key` and `--key-id pithyx-release-1`, which only the owner holds.
- `publish-sdk.sh` never moves `latest` to a prerelease; `pithyx-release publish-sdk --latest` adds `npm dist-tag add <package>@<version> latest` for all three packages, with the same `--otp`.

Steps that need a particular machine stay there: images need mmdebstrap and the build packages (a dev VM), stable signatures the release key. Updating a box from a file before publishing it is described under [Switching the update source](/docs/install/update-source/).
