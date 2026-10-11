#!/usr/bin/env bash
# Signs an update file (D338): an Ed25519 signature over the exact bytes of its manifest.json goes into the file as
# manifest.json.sig, then the file is unpacked again and checked as a box checks it (signature, every package's size
# and SHA-256, nothing unlisted).
#
#   tools/release/sign-update.sh <file.pithyxupdate> --key <pem file> --key-id <id> [--append]
#
# Keys (tools/release/create-key.sh): pithyx-beta-1 signs beta files and lives on the maintainer's dev machine in ~/.config/pithyx-dev/;
# pithyx-release-1 signs stable files and only the owner holds it. --append keeps the signatures already in the file
# (signing with an old and a new key during a rotation); otherwise a signature by another key is replaced. A key the
# box does not trust yet (missing from tools/release/keys.json) is checked with its own public half and named.
set -euo pipefail
export LC_ALL=C.UTF-8

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
file=""
key=""
key_id=""
append=false

die() {
  echo "sign-update: $*" >&2
  exit 1
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --key) key="$2"; shift 2 ;;
    --key-id) key_id="$2"; shift 2 ;;
    --append) append=true; shift ;;
    -h | --help) sed -n '2,11p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
    -*) die "unknown argument $1" ;;
    *) file="$(realpath "$1")"; shift ;;
  esac
done
[[ -f "$file" && "$file" == *.pithyxupdate ]] || die "name a .pithyxupdate file"
[[ -n "$key" && -n "$key_id" ]] || die "--key and --key-id are required"
[[ -r "$key" ]] || die "cannot read the key $key"
if [[ "$(stat -c %a "$key")" != 600 && "$(stat -c %a "$key")" != 400 ]]; then
  die "$key is readable by others (mode $(stat -c %a "$key")); chmod 600 it"
fi

unpacked="$(mktemp -d "$(dirname "$file")/.sign.XXXXXX")"
trap 'rm -rf "$unpacked"' EXIT
tar --extract --file="$file" --directory="$unpacked" --no-same-owner
[[ -f "$unpacked/manifest.json" && -d "$unpacked/debs" ]] || die "$file has no manifest.json or debs/"
channel="$(sed -n 's/^  "channel": "\(.*\)",$/\1/p' "$unpacked/manifest.json")"

sign_options=(--key-id "$key_id")
[[ "$append" == true ]] && sign_options+=(--append)
[[ "$append" == true ]] || rm -f "$unpacked/manifest.json.sig"
# The key goes to node through its environment, never as an argument that `ps` would show.
PITHYX_SIGNING_KEY="$(cat "$key")" node "$repo/tools/release/update-file.ts" sign "$unpacked/manifest.json" "${sign_options[@]}"
public="$(PITHYX_SIGNING_KEY="$(cat "$key")" node "$repo/tools/release/update-file.ts" public-key)"

"$repo/tools/release/pack-update.sh" "$unpacked" "$file.tmp"
mv "$file.tmp" "$file"
(cd "$(dirname "$file")" && sha256sum "$(basename "$file")" >"$(basename "$file").sha256")

rm -rf "$unpacked"
mkdir "$unpacked"
tar --extract --file="$file" --directory="$unpacked" --no-same-owner
verify_options=(--channel "$channel")
# A key of the keyring is checked against the public half the box knows, so a wrong key file fails here.
if ! grep -q "\"keyId\": \"$key_id\"" "$repo/tools/release/keys.json"; then
  echo "sign-update: $key_id is not in tools/release/keys.json; no box trusts it yet (public key $public)" >&2
  verify_options+=(--extra-key "$key_id:$public:$channel")
fi
node "$repo/tools/release/update-file.ts" verify-unpacked "$unpacked" "${verify_options[@]}" || die "the signed file does not verify"
echo "signed $file with $key_id ($channel) and verified it"
