#!/usr/bin/env bash
# Writes and signs the channel.json that names a signed update file as the newest of its channel (D338), and checks
# both as a box does:
#
#   tools/release/build-channel.sh <file.pithyxupdate> --key <pem file> --key-id <id> --sequence <n>
#                                  [--url <url>] [--out <dir>]
#
# The channel (stable or beta), version, minimum version and changelog come from the file's manifest; the file must be
# signed for that channel (tools/release/sign-update.sh). --sequence must be higher than the channel's current one: a
# box refuses a lower sequence than it saw, so an old channel.json cannot be replayed. --url says where the file lies
# relative to channel.json; the default fits GitHub releases, where channel.json lies on the release channel-<channel>
# and the file on beta-<version> (beta) or v<version> (stable): ../beta-<version>/<name>. The result is
# <out>/channel.json and channel.json.sig (default: next to the file).
set -euo pipefail
export LC_ALL=C.UTF-8

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
file=""
key=""
key_id=""
sequence=""
url=""
out=""

die() {
  echo "build-channel: $*" >&2
  exit 1
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --key) key="$2"; shift 2 ;;
    --key-id) key_id="$2"; shift 2 ;;
    --sequence) sequence="$2"; shift 2 ;;
    --url) url="$2"; shift 2 ;;
    --out) out="$(realpath -m "$2")"; shift 2 ;;
    -h | --help) sed -n '2,14p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
    -*) die "unknown argument $1" ;;
    *) file="$(realpath "$1")"; shift ;;
  esac
done
[[ -f "$file" && "$file" == *.pithyxupdate ]] || die "name a signed .pithyxupdate file"
[[ -n "$key" && -n "$key_id" && "$sequence" =~ ^[1-9][0-9]*$ ]] || die "--key, --key-id and --sequence are required"
[[ "$(stat -c %a "$key")" == 600 || "$(stat -c %a "$key")" == 400 ]] || die "$key is readable by others; chmod 600 it"
out="${out:-$(dirname "$file")}"
mkdir -p "$out"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
tar --extract --file="$file" --directory="$work" manifest.json manifest.json.sig 2>/dev/null \
  || die "$file carries no manifest.json.sig; sign it first"
channel="$(sed -n 's/^  "channel": "\(.*\)",$/\1/p' "$work/manifest.json")"
version="$(sed -n 's/^  "version": "\(.*\)",$/\1/p' "$work/manifest.json")"
node "$repo/tools/release/update-file.ts" verify-manifest "$work/manifest.json" --channel "$channel" \
  || die "the file's manifest does not verify for $channel"

name="$(basename "$file")"
if [[ -z "$url" ]]; then
  if [[ "$channel" == beta ]]; then
    url="../beta-$version/$name"
  else
    url="../v$version/$name"
  fi
fi
node "$repo/tools/release/update-file.ts" channel "$work/manifest.json" --name "$name" --url "$url" \
  --size "$(stat -c %s "$file")" --sha256 "$(sha256sum "$file" | cut -d' ' -f1)" --sequence "$sequence" \
  >"$out/channel.json"
rm -f "$out/channel.json.sig"
PITHYX_SIGNING_KEY="$(cat "$key")" node "$repo/tools/release/update-file.ts" sign "$out/channel.json" --key-id "$key_id"
node "$repo/tools/release/update-file.ts" verify-channel "$out/channel.json" --channel "$channel" \
  || die "channel.json does not verify for $channel with the keys of tools/release/keys.json"
echo "wrote $out/channel.json ($channel, sequence $sequence, $version)"
