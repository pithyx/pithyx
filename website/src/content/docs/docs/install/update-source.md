---
title: 'Switching the update source (forks)'
description: 'How a box follows the updates of a fork instead of the Pithyx project, what that means, the way back, and what a fork has to publish.'
status: ready
audience: admin
sidebar:
  order: 6
---

A Pithyx box takes its updates from exactly one update source, by default the Pithyx project. Pithyx is free software, so a box may follow a fork instead, but only by a deliberate act at the box's console: the recovery menu is the only place that switches the update source and the keys the box trusts, never Settings, the API or an uploaded file. A taken-over administrator account therefore cannot redirect a box's updates. After a switch the box trusts only the new source's keys, until someone switches back in the same menu.

## For administrators

### What a switch means

- Whoever signs the source's updates can change everything on the box: an update runs as root. Switch only to a source you trust.
- The box refuses updates of the Pithyx project as long as it follows another source.
- The fork may change the system and its database so that going back to the Pithyx project needs a reinstall. A Pithyx daemon refuses a database with schema versions it does not know.
- Pools and their files are never touched by the switch.
- The switch lives on the system disk. A rollback to a snapshot taken before the switch returns to the source of that snapshot. It is not part of the configuration backup.

### Switching

You need the source's `pithyx-source.json`, which the fork publishes together with its public keys. The menu takes it in one of three ways:

- **From Settings**: Settings > **Updates**, **Update source**, **Save source file for the recovery menu**. The box checks the file and keeps it, but nothing changes until it is chosen at the console. This helps when you reach the console only through remote management. **Remove** forgets the saved file.
- **From a USB stick**: put the file (`pithyx-source.json`, or a name starting with `pithyx-source` and ending in `.json`) into the stick's top folder. A BMC's virtual media works too.
- **Typed**: name, channel URL and each key with its id and channels.

Then:

1. Restart the box and choose **Pithyx recovery** in the boot menu (key `r`).
2. Choose **[4] Change the update source**, then **[1] Read pithyx-source.json from a USB stick**, **[2] Use the file saved in Settings** or **[3] Type the source**.
3. The menu shows the name, the channel URL and every public key in full. Compare every key with the one the source publishes.
4. Type the source's name to switch. Anything else changes nothing.
5. Restart with **[0] Restart**. The box follows the new source from this start.

After the restart the administrators get the notification "The update source is now {name}", and Settings > **Updates** shows the source with the notice "This box follows {name}, not the Pithyx project." If nobody on your side made the switch, switch back at once.

A file that does not pass the check is refused with [`update.source.invalid`](/docs/reference/errors/#update.source.invalid).

### The way back

In the recovery menu choose **[4] Change the update source**, then **[4] Back to the Pithyx project**, and type `Pithyx`. After the restart the box trusts only the built-in keys of the project again and reads its releases. Updates install only when they are newer than the installed version, so a box that ran a fork with a higher version number waits for a newer Pithyx release, and a fork that changed the database may need a reinstall (see [Recovery and reinstall](/docs/install/recovery/)).

## For fork maintainers

A box that switches to your fork reads your channels exactly as it reads the project's: the same file formats, the same checks and the same rules. You publish three things.

### 1. A signing key

`tools/release/create-key.sh <key id> <file>` creates an Ed25519 key, writes the private half as PEM with mode 600 and prints the public half. Keep the private key off the repository and out of logs. A key id is lowercase letters, digits, `.` and `-`, at most 64 characters. A source may name up to four keys, each for `stable`, `beta` or both, so you can keep a reserve key or rotate: `tools/release/sign-update.sh --append` signs with a second key and keeps the first signature.

### 2. Update files and channels

Build and sign them with the release scripts of the repository (the release tool runs steps 1 to 3 with the right baseline: `tools/release/pithyx-release update --version <version> --build-image --sign`, see [Releasing](/docs/project/releasing/)):

1. `image/build.sh --version <version>` builds the image and the list of its packages.
2. `tools/release/build-update.sh --version <version> --channel stable|beta [--minimum <version>] [--since <packages file> | --full]` builds `pithyx-<version>-amd64.pithyxupdate` (unsigned). It carries every package whose version differs from the package list of the minimum version (`image/build.sh` writes it as `image/out/pithyx-<version>-amd64.packages`; the script looks for the minimum version's list in `image/baseline/pithyx-<minimum>-amd64.packages`, a folder that does not exist in the repository yet, so `--since` names one directly), so keep the package list of every release you publish. `--full` puts every package in.
3. `tools/release/sign-update.sh <file> --key <pem file> --key-id <id>` signs the manifest and checks the file as a box does.
4. `tools/release/build-channel.sh <file> --key <pem file> --key-id <id> --sequence <n> [--url <url>]` writes and signs `channel.json` and `channel.json.sig`.

To try an update before you publish it, install the image of the older version on a test box, switch it to the **Beta** channel in Settings > **Updates**, and upload the file signed with a beta key under **Install from a file**. `build-update.sh` reads the packages of the last `image/build.sh` run, so build the image of the version first and the update file right after it.

Publish `channel.json` and `channel.json.sig` at your channel URL, with `{channel}` replaced by `stable` or `beta`, and the update file where `channel.json` points to (its URL is relative to `channel.json`). With GitHub releases, the defaults of `build-channel.sh` fit the layout of the project: `channel.json` on a release `channel-<channel>`, the file on `beta-<version>` or `v<version>`. Everything must be reachable over https without credentials.

The rules the box applies:

- **Versions** are SemVer. A box installs only a version newer than its installed one, so your first release must be newer than the Pithyx version on the boxes that switch to you.
- **Minimum version**: a box older than a file's minimum version refuses it and needs a version in between first.
- **Sequence**: every publish of a channel raises `--sequence` by at least one. A box refuses a lower sequence than it saw. After a switch the box forgets the old source's sequences, so your channels may start at 1.
- **Channels**: a stable box installs only stable files, a beta box both. A key signs only for the channels the source names for it.
- **Database**: if your version adds database migrations, a box that later goes back to the Pithyx project may need a reinstall. Say so to your users.

### 3. `pithyx-source.json`

```sh
node tools/release/update-file.ts source --name "<name>" --url "https://<host>/<path>/channel-{channel}/channel.json" \
  --key <key id>:<public key>:stable,beta > pithyx-source.json
node tools/release/update-file.ts verify-source pithyx-source.json
```

The file has `sourceVersion` 1, a `name` of 1 to 64 printable ASCII characters (it is typed at the console to confirm), an https `url` of at most 512 characters with `{channel}`, and one to four `keys`, each with `keyId`, `publicKey` (32 bytes in base64) and `channels`. The box refuses unknown fields, files over 64 KB and a key id that appears twice. The schema is `tools/release/schema/update-source.v1.schema.json`.

Publish the file together with the public keys in a place your users can check, so they can compare the keys the console shows.
