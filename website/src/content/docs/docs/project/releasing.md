---
title: 'Releasing'
description: 'The steps of a release and the release tool tools/release/pithyx-release: building the ISO and update files, signing them and publishing them and the npm packages.'
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
- The baseline of an update is `--since <file>`, `--full`, or the package list of `--minimum` (default `1.0.0`) from `image/baseline/`, else from `image/out/`. Without one it names the command that keeps it.
- The channel is beta for a prerelease version and stable otherwise. A beta file is signed with `~/.config/pithyx-dev/pithyx-beta-1.pem`; a stable file needs `--key` and `--key-id pithyx-release-1`, which only the owner holds.
- `publish-sdk.sh` never moves `latest` to a prerelease; `pithyx-release publish-sdk --latest` adds `npm dist-tag add <package>@<version> latest` for all three packages, with the same `--otp`.

Steps that need a particular machine stay there: images need mmdebstrap and the build packages (a dev VM), stable signatures the release key. Updating a box from a file before publishing it is described under [Switching the update source](/docs/install/update-source/).

## Releasing a version

The steps of a release `X.Y.Z`, in order. Building and testing run on a dev VM; merging into `beta` and `stable`, the tag, the stable signatures and the npm publish are the maintainer's.

1. **Branches.** The changes are on `dev` with green CI, including an E2E run (`gh workflow run CI --ref dev`). A pull request `dev` → `beta`, then `beta` → `stable`; both need green CI (the ruleset `protected-branches`).
2. **Version.** The daemon's version in `daemon/Directory.Build.props`, the `CHANGELOG.md` section of the version and the docs (`since`, version numbers) are on `dev` before step 1.
3. **Image.** On the dev VM, from the commit on `stable`: `pithyx-release iso --version X.Y.Z`. Then `image/test-install.sh --iso image/out/pithyx-X.Y.Z-amd64.iso --mirror` installs it in QEMU and checks the installed system.
4. **Update file.** `pithyx-release update --version X.Y.Z` builds the `.pithyxupdate` against the baseline (`image/baseline/`), and `pithyx-release baseline --version X.Y.Z` keeps the new package list for the next update (commit it on `dev`).
5. **GPL sources.** `pithyx-release sources --version X.Y.Z` packs the Debian sources of the image as `pithyx-X.Y.Z-sources-<n>.tar`.
6. **Tag.** The maintainer tags the commit the image was built from: `git tag -a vX.Y.Z <commit> -m "Pithyx X.Y.Z <codename>"`, `git push origin vX.Y.Z`.
7. **Signing (maintainer, own machine).** `pithyx-release sign <file> --key <pithyx-release-1.pem> --key-id pithyx-release-1`, then `pithyx-release channel <file> --key <pem> --key-id pithyx-release-1 --sequence <n>` with the sequence of `channel-stable` plus one. The release key never leaves the maintainer's machine; the ISO itself carries no signature, only its checksum.
8. **Publish.** The release `vX.Y.Z` with the ISO, its `.sha256`, the update file with its `.sha256` and the sources parts (`gh release create vX.Y.Z --verify-tag ...`); then `channel.json` and `channel.json.sig` on the release `channel-stable` (`gh release upload channel-stable ... --clobber`). Boxes on the stable channel find the update at their next daily check and notify their administrators.
9. **Website.** Pushes to `stable` publish the docs; Actions, Pages, Run workflow on `stable` publishes by hand.
10. **npm (when the packages changed).** `pithyx-release publish-sdk`, and for a stable version `--latest`. With a security key instead of one-time codes, leave out `--otp`: npm prints a link to confirm in the browser.

A beta version (`X.Y.Z-beta.N`) goes the same way up to `beta` and stops there: `pithyx-release update --version X.Y.Z-beta.N --sign` signs with the beta key, and `pithyx-release publish-beta <file>` publishes it on `beta-X.Y.Z-beta.N` and `channel-beta`.
