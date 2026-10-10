---
title: 'pithyx catalog sign'
description: 'pithyx catalog sign signs index.json of a catalog branch with the Ed25519 key in PITHYX_SIGNING_KEY.'
status: ready
audience: developer
sidebar:
  order: 9
---

`pithyx catalog sign` signs the exact bytes of `index.json` in a branch folder with an Ed25519 private key and writes `index.json.sig`. Boxes check this signature against the keys built into the daemon for the project's catalogs; custom catalogs are not verified.

## Synopsis

<!-- generated:cli-synopsis catalog sign -->

```sh
pithyx catalog sign <branch folder> --key-id <id> [--append]
```

<!-- /generated -->

## Options

<!-- generated:cli-options catalog sign -->

| Option     | Value  | Type    | Default  | Description                                  |
| ---------- | ------ | ------- | -------- | -------------------------------------------- |
| `--key-id` | `<id>` | string  | required | Not described in `pithyx --help`; see below. |
| `--append` | –      | boolean | off      | Not described in `pithyx --help`; see below. |

<!-- /generated -->

- `--key-id <id>`: the name of the key as the box's keyring knows it, for example `pithyx-next-1`.
- `--append`: keeps the signatures of other key ids in `index.json.sig`, for a key rotation. A signature by the same key id is replaced. Without it the file holds only the new signature.

The key comes only from the environment variable `PITHYX_SIGNING_KEY`: an Ed25519 private key in PEM (PKCS #8, as `openssl genpkey -algorithm ed25519` writes it). It is never accepted as an option, so it does not end up in a shell history or a process list.

## Output file

```json
{
  "signatureVersion": 1,
  "signatures": [{ "keyId": "pithyx-next-1", "signature": "<base64>" }]
}
```

## Files

| File                             | Access                        |
| -------------------------------- | ----------------------------- |
| `<branch folder>/index.json`     | read                          |
| `<branch folder>/index.json.sig` | read with `--append`, written |

## Exit codes

| Code | When                                                                          |
| ---- | ----------------------------------------------------------------------------- |
| 0    | `Signed <branch>/index.json with <id>.`                                       |
| 1    | The key is not an Ed25519 private key in PEM, or `index.json` cannot be read. |
| 2    | No branch folder, no `--key-id`, or `PITHYX_SIGNING_KEY` is unset or empty.   |

## Findings

None.

## Examples

```sh
openssl genpkey -algorithm ed25519 -out next.pem
PITHYX_SIGNING_KEY="$(cat next.pem)" pithyx catalog sign catalog-next --key-id example-next-1
```
