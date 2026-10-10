---
title: 'pithyx catalog verify'
description: 'pithyx catalog verify checks a catalog branch as a box does: the signature against given keys, the index and every file.'
status: ready
audience: developer
sidebar:
  order: 10
---

`pithyx catalog verify` checks a branch folder of a catalog as a box does before it uses it: the signature (when you give keys), the index for this catalog and channel, and every file of every version against its entry. Catalog CI runs it after signing.

## Synopsis

<!-- generated:cli-synopsis catalog verify -->

```sh
pithyx catalog verify <branch folder> --channel stable|beta [--key <id>=<base64>]... [--catalog <file>]
```

<!-- /generated -->

## Options

<!-- generated:cli-options catalog verify -->

| Option      | Value          | Type               | Default        | Description                                  |
| ----------- | -------------- | ------------------ | -------------- | -------------------------------------------- |
| `--catalog` | `<file>`       | string             | `catalog.json` | The catalog.json                             |
| `--channel` | `stable\|beta` | string             | required       | Not described in `pithyx --help`; see below. |
| `--key`     | `<id>`         | string, repeatable | –              | Not described in `pithyx --help`; see below. |

<!-- /generated -->

- `--channel stable|beta`: the channel the index must declare.
- `--key <id>=<base64>` (repeatable): a trusted public key, the raw 32-byte Ed25519 key in base64, trusted for the given channel. With at least one key, `index.json.sig` must carry a valid signature by one of them. Without keys the signature is not checked, as for a custom catalog.
- `--catalog <file>`: the catalog file whose id the index must name (default `catalog.json`).

## Files

| File                                                                 | Access |
| -------------------------------------------------------------------- | ------ |
| `catalog.json` (or `--catalog`)                                      | read   |
| `<branch folder>/index.json`, `index.json.sig`                       | read   |
| every file of `<branch folder>/apps/<id>/<version>/` the index lists | read   |

## Exit codes

| Code | When                                                                                                                |
| ---- | ------------------------------------------------------------------------------------------------------------------- |
| 0    | `<branch> is valid and signed.`, or `<branch> is valid (signature not checked).` without keys.                      |
| 1    | A finding, `catalog.json` is not valid, `--channel` is missing or not `stable` or `beta`, or a `--key` without `=`. |
| 2    | No branch folder.                                                                                                   |

## Findings

`store.catalog.invalid_json` when `index.json` is missing, the other `store.catalog.*` codes of the index (`signature_invalid` with the reason `missing`, `malformed`, `untrusted` or `mismatch`), and `store.package.hash_mismatch` for every file that differs. See [Findings](/docs/reference/cli/findings/).

## Examples

```sh
pithyx catalog verify catalog-next --channel beta --key "example-next-1=$(cat next.pub.b64)"
```
