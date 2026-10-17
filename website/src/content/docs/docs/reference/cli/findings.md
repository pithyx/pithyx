---
title: 'Findings'
description: 'Every finding the pithyx command can report, with its level and message, generated from the CLI and the box translations.'
status: ready
audience: developer
sidebar:
  order: 11
---

A finding is one problem the `pithyx` command reports, printed to standard error as `<level> <code>: <message>`. An error stops [`pack`](/docs/reference/cli/pack/) and makes the command exit with 1; a warning does not. Most codes are the box's own error codes with the box's message, so the CLI says what the box would say; the codes in `cli.*` exist only in the CLI, because the box sees a package, not the project it was built from. This list is generated from `packages/cli/src/issues.ts`, `findings.ts` and `i18n/<locale>/errors.json`. `{name}` marks a parameter; where a message depends on a parameter, its variants follow below it.

## App packages

Reported by [`validate`](/docs/reference/cli/validate/) and [`pack`](/docs/reference/cli/pack/), the Compose codes also by [`build`](/docs/reference/cli/build/), and by [`catalog check`](/docs/reference/cli/catalog-check/) and [`catalog index`](/docs/reference/cli/catalog-index/) for the apps of a catalog.

<!-- generated:cli-findings app -->

- [`app.compose.key_unsupported`](/docs/reference/errors/#app.compose.key_unsupported) (error): The app's Compose file uses {key} at {path}, which Pithyx does not allow.
- [`app.compose.reference_unknown`](/docs/reference/errors/#app.compose.reference_unknown) (error): The app's backend refers to {name} at {path}, which is not declared.
- [`app.compose.right_missing`](/docs/reference/errors/#app.compose.right_missing) (error): The app's Compose file needs the right {right} at {path}, which its manifest does not declare.
- [`app.compose.right_unused`](/docs/reference/errors/#app.compose.right_unused) (error): The app's manifest declares the right {right} at {path}, which its Compose file does not use.
- [`app.compose.storage_mismatch`](/docs/reference/errors/#app.compose.storage_mismatch) (error): The storage {storage} does not match the Compose file at {path}: {reason}.
  - `reason` = `not_mounted`: the service does not mount it
  - `reason` = `not_listed`: the service mounts it without being listed in the manifest
  - `reason` = `read_only`: it is read-only and must be mounted with :ro
  - `reason` = `other`: the manifest and the Compose file disagree
- [`app.compose.value_invalid`](/docs/reference/errors/#app.compose.value_invalid) (error): The app's Compose file has a value Pithyx refuses at {path}: {reason}.
  - `reason` = `required`: it is missing
  - `reason` = `type`: it has the wrong type
  - `reason` = `format`: its format is not valid
  - `reason` = `range`: it is out of range
  - `reason` = `unpinned`: the image is not pinned with @sha256
  - `reason` = `interpolation`: it uses $ for a variable (write $$ for a dollar sign)
  - `reason` = `no_value`: the variable has no value
  - `reason` = `duplicate`: it appears twice
  - `reason` = `reserved`: it is reserved for Pithyx
  - `reason` = `host_path`: it mounts a folder of the host
  - `reason` = `anonymous`: the volume has no name
  - `reason` = `cycle`: the services depend on each other in a circle
  - `reason` = `image_and_build`: the service has both image and build, use one of them
  - `reason` = `other`: it is not valid
- [`app.compose.yaml_invalid`](/docs/reference/errors/#app.compose.yaml_invalid) (error): The app's Compose file cannot be read: {reason}.
  - `reason` = `too_large`: it is larger than 64 KB
  - `reason` = `not_utf8`: it is not UTF-8
  - `reason` = `character`: it contains a tab or another character that is not allowed
  - `reason` = `syntax`: it is not valid YAML
  - `reason` = `documents`: it must contain exactly one document
  - `reason` = `too_deep`: it is nested too deeply
  - `reason` = `directive`: it contains a YAML directive
  - `reason` = `anchor`: it uses anchors or aliases
  - `reason` = `tag`: it uses YAML tags
  - `reason` = `duplicate_key`: {path} appears twice
  - `reason` = `merge_key`: it uses a merge key at {path}
  - `reason` = `key_not_string`: a key at {path} is not text
  - `reason` = `other`: it breaks a rule for Compose files
- [`app.elevated.unsupported`](/docs/reference/errors/#app.elevated.unsupported) (error): The app asks for the right {right}, which this version of Pithyx does not grant.
- [`app.icon.invalid`](/docs/reference/errors/#app.icon.invalid) (error): The app's icon cannot be used: {reason}.
  - `reason` = `too_large`: it is larger than 64 KB
  - `reason` = `malformed`: it is not a valid SVG file
  - `reason` = `not_svg`: it is not an SVG image
  - `reason` = `viewbox`: its viewBox is missing or not square
  - `reason` = `doctype`: it contains a DOCTYPE
  - `reason` = `processing_instruction`: it contains a processing instruction
  - `reason` = `element_forbidden`: it contains the element {element}
  - `reason` = `event_attribute`: it contains the event attribute {attribute}
  - `reason` = `external_href`: it links to something outside the file
  - `reason` = `style_import`: its styles use @import
  - `reason` = `style_url`: it loads something outside the file with url()
  - `reason` = `style_escape`: its styles contain escapes
  - `reason` = `other`: it breaks a rule for icons
- [`app.manifest.cron_invalid`](/docs/reference/errors/#app.manifest.cron_invalid) (error): The schedule at {path} is not a valid cron expression: {cron}
- [`app.manifest.duplicate`](/docs/reference/errors/#app.manifest.duplicate) (error): The manifest has {value} twice (at {path}).
- [`app.manifest.id_invalid`](/docs/reference/errors/#app.manifest.id_invalid) (error): The app id {id} is not valid. It is the author's domain reversed plus the app name, for example com.example.chat.
- [`app.manifest.id_reserved`](/docs/reference/errors/#app.manifest.id_reserved) (error): The app id {id} is reserved for the apps that come with Pithyx.
- [`app.manifest.invalid_json`](/docs/reference/errors/#app.manifest.invalid_json) (error): The app's manifest (pithyx.json) is not valid JSON.
- [`app.manifest.pithyx_invalid`](/docs/reference/errors/#app.manifest.pithyx_invalid) (error): The Pithyx version range {pithyx} in the manifest is not valid.
- [`app.manifest.reference_unknown`](/docs/reference/errors/#app.manifest.reference_unknown) (error): The manifest refers to {name} at {path}, which it does not declare.
- [`app.manifest.schema_violation`](/docs/reference/errors/#app.manifest.schema_violation) (error): The app's manifest is not valid at {path} ({keyword}).
- [`app.manifest.sdk_invalid`](/docs/reference/errors/#app.manifest.sdk_invalid) (error): The SDK version range {sdk} in the manifest is not valid.
- [`app.manifest.version_unsupported`](/docs/reference/errors/#app.manifest.version_unsupported) (error): This app needs a newer version of Pithyx (manifest version {version}, this box reads version {supported}).
- [`app.manifest.window_size_invalid`](/docs/reference/errors/#app.manifest.window_size_invalid) (error): The app's smallest window size is larger than its default size.
- [`app.package.invalid`](/docs/reference/errors/#app.package.invalid) (error): The file is not a valid app package: {reason}.
  - `reason` = `gzip`: it is not gzip-compressed or it is damaged
  - `reason` = `format`: it is not a tar archive this box reads
  - `reason` = `checksum`: a header is damaged
  - `reason` = `truncated`: it ends too early
  - `reason` = `trailing_data`: there is data after its end
  - `reason` = `entry_type`: {path} is a link or a special file
  - `reason` = `extended_header`: it has an extended header that is not allowed
  - `reason` = `name_mismatch`: the long name of {path} does not match its own name
  - `reason` = `other`: it cannot be read
- [`app.package.layout_invalid`](/docs/reference/errors/#app.package.layout_invalid) (error): The app package is not valid: {reason}.
  - `reason` = `missing`: {path} is missing
  - `reason` = `unsafe`: the path {path} is not allowed
  - `reason` = `duplicate`: {path} is in it twice
  - `reason` = `unexpected`: {path} does not belong in an app package
  - `reason` = `reserved`: {path} uses a name reserved for the backend
  - `reason` = `other`: {path} is not allowed

<!-- /generated -->

## Projects

Only the CLI reports these. `cli.backend.unbuilt` comes from the Vite plugin after `vite build`, for an app whose backend has only its source form `compose.dev.yaml` yet, as in a catalog before its CI builds the images.

<!-- generated:cli-findings cli -->

- `cli.backend.unbuilt` (warning): {path} is not built yet: run pithyx build, or let the catalog build it from the source form.
- `cli.compose.healthcheck_redundant` (warning): Service {service} has a Compose healthcheck next to the health of the manifest. Docker starts a process in the container for every check, which delays every start and keeps the idle backend busy. Remove it and let health decide.
- `cli.frontend.missing` (error): The frontend build {folder} does not exist. Build the app first, or name the folder with --frontend.
- `cli.frontend.skipped` (warning): {path} is left out of the package: hidden files and folders cannot be in an app package.
- `cli.icon.template` (warning): icon.svg is still the template icon (the Pithyx box). Replace it with the icon of your app.
- `cli.lock.invalid` (error): {path} is not a valid images lock. Run pithyx lock to write it again.
- `cli.lock.missing` (error): {path} is missing. Run pithyx lock to resolve the images of the backend.
- `cli.lock.platform_missing` (warning): {image} has no image for {arch}; the app will not install on {arch} boxes.
- `cli.lock.stale` (error): {path} does not match the Compose file ({reason}). Run pithyx lock.
  - `reason` = `missing`: {image} is not locked
  - `reason` = `unused`: {image} is not used
  - `reason` = `other`: {image} differs
- `cli.manifest.missing` (error): pithyx.json does not exist in {folder}. Run the command in the project folder of an app, or name that folder.

<!-- /generated -->

## Catalogs

Reported by the [`catalog`](/docs/reference/cli/catalog-check/) commands.

<!-- generated:cli-findings store -->

- [`store.catalog.architecture_missing`](/docs/reference/errors/#store.catalog.architecture_missing) (error): The catalog lists an image at {path} without the architecture {architecture}.
- [`store.catalog.data_version_decreased`](/docs/reference/errors/#store.catalog.data_version_decreased) (error): The catalog lowers the data version at {path} to {dataVersion}, after {previous} in an older version.
- [`store.catalog.id_reserved`](/docs/reference/errors/#store.catalog.id_reserved) (error): The catalog uses the id {id} at {path}, which only the official Pithyx catalog may use.
- [`store.catalog.invalid_json`](/docs/reference/errors/#store.catalog.invalid_json) (error): The catalog's file {file} cannot be read as JSON.
- [`store.catalog.mismatch`](/docs/reference/errors/#store.catalog.mismatch) (error): The catalog's index has {value} at {path}, but this box expects {expected}.
- [`store.catalog.rule_violation`](/docs/reference/errors/#store.catalog.rule_violation) (error): The pull request breaks a catalog rule: {rule}.
  - `rule` = `one_app`: {path} lies outside the one app folder a pull request may change
  - `rule` = `required_file`: {path} is missing
  - `rule` = `file_type`: {path} is a kind of file a catalog does not accept
  - `rule` = `binary`: {path} is binary or not UTF-8
  - `rule` = `minified`: {path} looks minified or generated
  - `rule` = `media`: {path} is not the image or font its name says
  - `rule` = `unpinned`: line {line} of {path} uses the image {image}, which is not pinned with @sha256
  - `rule` = `screenshot`: {path} is not a screenshot directly in screenshots/ as a PNG, JPEG or WebP file with a lower case name of at most 1 MB
  - `rule` = `screenshots`: {path} holds more than {limit} screenshots
  - `rule` = `app_size`: {path} is larger than {limit} bytes
  - `rule` = `folder_id`: the folder of {path} must be named like the app's id {id}
  - `rule` = `manifest_field`: the manifest {path} needs the field {field}
  - `rule` = `license`: {license} in {path} is not an OSI approved license
  - `rule` = `version_not_higher`: version {version} in {path} is not higher than the published {published}
  - `rule` = `maintainer`: {author} is not a maintainer of the app
  - `rule` = `maintainers_invalid`: {path} is not a valid list of maintainers
  - `rule` = `other`: {path} breaks a rule
- [`store.catalog.schema_violation`](/docs/reference/errors/#store.catalog.schema_violation) (error): The catalog's file {file} is not valid at {path} ({keyword}).
- [`store.catalog.sequence_old`](/docs/reference/errors/#store.catalog.sequence_old) (error): The catalog's index is older (sequence {sequence}) than one this box already checked ({known}), so the box keeps its copy.
- [`store.catalog.signature_invalid`](/docs/reference/errors/#store.catalog.signature_invalid) (error): The catalog's index is not signed correctly ({reason}), so the box keeps its last checked copy.
  - `reason` = `missing`: the signature is missing
  - `reason` = `malformed`: the signature file is not valid
  - `reason` = `untrusted`: no signature is by a key this box trusts for its channel
  - `reason` = `mismatch`: a signature does not match the index
  - `reason` = `other`: the signature does not prove it
- [`store.catalog.version_unsupported`](/docs/reference/errors/#store.catalog.version_unsupported) (error): The catalog's file {file} has format version {version}, this box reads version {supported}. Update Pithyx to use this catalog.
- [`store.package.hash_mismatch`](/docs/reference/errors/#store.package.hash_mismatch) (error): A file of the catalog does not match its index ({path}). The box does not install it.

<!-- /generated -->
