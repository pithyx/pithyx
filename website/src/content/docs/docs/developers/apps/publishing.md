---
title: 'Publishing to a catalog'
description: 'Getting an app into the official or community catalog, or running an own catalog.'
status: draft
audience: developer
sidebar:
  order: 5
---

A catalog is a Git repository that lists apps for the Store. Pithyx has two project catalogs, `pithyx/apps-official` and `pithyx/apps-community`, and anyone can run their own, made from the template repository `pithyx/catalog-template`. A box clones the catalogs it uses, verifies them and never builds anything.

## Catalogs

- **Official** (`pithyx/apps-official`) is the only source of `org.pithyx.*` apps besides the system image. **Community** (`pithyx/apps-community`) is off by default. An administrator can add **Own** catalogs.
- **Source.** Each app lives in `apps/<id>/` on the `master` branch. CI builds the frontend and the images, pushes the images to `ghcr.io/<owner>/apps/<id>/<service>:<version>` (never overwritten, linked by digest), and writes two CI-only branches: `catalog-next` (the beta channel) and `catalog` (the stable channel). The stable branch is updated only after a manual approval.
- **Files.** `catalog.json` at the root names the catalog. Each branch holds `index.json` and, for project catalogs, `index.json.sig`. Per version, the index lists hashes and sizes of the package, manifest, icon and screenshots, the images, the architectures, the `pithyx` range, the `dataVersion`, the license and the source commit. The files are at `apps/<id>/<version>/`: `<id>-<version>.pithyxapp`, `pithyx.json`, `icon.svg` and `screenshots/`.
- **Sequence.** Each publish raises the index `sequence` by one. The box refuses a lower number ([`store.catalog.sequence_old`](/docs/reference/errors/#store.catalog.sequence_old)).
- **Signature.** Ed25519 over the exact bytes of `index.json`. A box accepts a project catalog if at least one signature comes from a key it trusts for the channel and every signature by a trusted key verifies. Unknown key ids are ignored, which allows key rotation. A failed check is [`store.catalog.signature_invalid`](/docs/reference/errors/#store.catalog.signature_invalid). Custom catalogs are not signed and the Store marks their apps as not verified.
- **On the box.** Catalogs are shallow clones in `/var/lib/pithyx/catalogs/<id>/`, fetched with git over https or ssh (3 minute timeout, no hooks, no submodules). A failed fetch keeps the last verified copy. Every file is checked against the index hash.
- **Channels and updates.** The Store settings have a channel (stable reads `catalog`, beta reads `catalog-next`), an update policy (automatic, notify or none; default notify), a daily check time (default 03:00 with a random delay of up to 60 minutes) and a per-app override. An update that needs new permissions, root or elevated rights is never installed automatically.

## The review

Every pull request to a project catalog is reviewed by the project: the app id follows the reversed-domain rule, no foreign brands, no `org.pithyx.*` in the community catalog, and the app has to be worth having. How long a review takes, and the exact process for outside contributors, is not documented.

Machine rules run on every pull request (`pithyx catalog check`, see below):

- Each app has a `maintainers.json` with 1 to 20 GitHub logins. A pull request that changes an app must come from one of the maintainers as the file stood before the change. A new app's file must name its author.
- Workflows: `check.yml` (pull requests, read only), `publish.yml` (pushes to `master` write `catalog-next`) and `promote.yml` (manual, writes `catalog` after approval in the environment `catalog-release`). Runs from outside contributors wait for approval.

## Rules

From `sdk/schema/catalog.v1.rules.json`:

- One app folder per pull request, under `apps/`. At most 5 MiB per app folder and 1000 characters per line.
- Required files: `pithyx.json`, `icon.svg`, `maintainers.json` and one of `LICENSE`, `LICENSE.md`, `LICENSE.txt`. The manifest needs `license` and `pithyx`.
- Up to 8 screenshots in `screenshots/`, each at most 1 MiB, named `[a-z0-9][a-z0-9_-]{0,62}` with `.png`, `.jpg` or `.webp`.
- Files are UTF-8 text or listed media (gif, jpeg, png, webp, woff2) whose content matches the extension. Minified files (`.min.js` and similar) and unlisted files are refused.
- Every `FROM` and `COPY --from` in a Dockerfile is pinned by `@sha256` or is `scratch`.
- The license is an SPDX expression of OSI-approved licenses; `LicenseRef-` is refused.
- Every image in the index has every architecture it lists ([`store.catalog.architecture_missing`](/docs/reference/errors/#store.catalog.architecture_missing)), and `dataVersion` never decreases.
- Builds are as reproducible as possible: BuildKit is pinned by digest in the workflows and `SOURCE_DATE_EPOCH` comes from the last commit.

## An own catalog

1. Create a repository from `pithyx/catalog-template`. The publish job refuses the template's placeholder id.
2. Put your apps in `apps/<id>/`. The CLI commands run in the repository root: `pithyx catalog check`, `pending`, `index`, `sign` and `verify` (see [@pithyx/cli](/docs/sdk/cli/#pithyx-catalog)).
3. In the Store, open the **Own** tab and add the repository by URL.
   - A public `https://host/path` URL is fetched at once.
   - A private repository uses an SSH URL such as `git@host:owner/repo.git`. The box creates an SSH deploy key and shows its public half; add it to the repository as a read-only key, then fetch.
   - Images in a private registry need a read token (`user:token`). The Store has no field for it; set it through the API with `PATCH /api/store/catalogs/{key}` and `registryToken`.
4. URLs use https without credentials, or ssh forms (`user@host:path`, `ssh://user@host[:port]/path`). Anything else is [`store.catalog.url_invalid`](/docs/reference/errors/#store.catalog.url_invalid).

Own catalogs may link images from any registry, by digest, and follow the global update policy. Signing is optional: the publish workflow signs only if the repository defines `SIGNING_KEY`. The box verifies signatures only for the project catalogs.

Packages that a workflow pushes to GitHub's container registry start as private, even from a public repository; make each package public in the registry's web interface or boxes cannot pull the images anonymously.

Removing an own catalog leaves its installed apps in place without updates. The project catalogs can be switched off but not removed.
