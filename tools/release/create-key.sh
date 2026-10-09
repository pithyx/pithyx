#!/usr/bin/env bash
# Creates an Ed25519 key that signs update files and channels (D338) and prints its public half for the box's keyring
# (tools/release/keys.json and UpdateKeys in the daemon, with the channels the key signs for).
#
#   tools/release/create-key.sh <key id> <file>
#
# The private key goes to <file> as PEM with mode 600 and never into the repository, a chat or a log. pithyx-beta-1
# lives on dev01 in ~/.config/pithyx-dev/; pithyx-release-1 (stable) only the owner holds. The script refuses to
# overwrite an existing file.
set -euo pipefail

die() {
  echo "create-key: $*" >&2
  exit 1
}

[[ "${1:-}" == -h || "${1:-}" == --help ]] && { sed -n '2,10p' "$0" | sed 's/^# \{0,1\}//'; exit 0; }
[[ $# -eq 2 ]] || die "usage: create-key.sh <key id> <file>"
key_id="$1"
file="$2"
[[ "$key_id" =~ ^[a-z0-9][a-z0-9._-]{0,63}$ ]] || die "the key id must match ^[a-z0-9][a-z0-9._-]{0,63}$"
[[ ! -e "$file" ]] || die "$file exists; a key is never overwritten"
command -v openssl >/dev/null || die "openssl not found"

umask 077
openssl genpkey -algorithm ed25519 -out "$file"
chmod 600 "$file"
public="$(openssl pkey -in "$file" -pubout -outform DER | tail -c 32 | base64)"
echo "created $file"
echo "public key of $key_id: $public"
