#!/usr/bin/env bash
# Signs an update file that another machine built, on the machine that holds the key (D338, D376): fetches the file
# and its checksum, checks the checksum, signs the file, writes and signs channel.json with the next sequence and puts
# all of it back next to the file in signed/. With --publish it also uploads the file to the release of its version
# and channel.json to channel-<channel>.
#
#   tools/release/sign-remote.sh --from <[user@]host:dir | dir> --version <v> --key <pem file> --key-id <id>
#                                [--sequence <n>] [--repo <owner/name>] [--work <dir>] [--publish] [--yes]
#
# --from is the build machine's output folder (image/out of its checkout), reached with scp, or a local folder. The
# connection goes only from this machine to the build machine; the key never leaves this one. --sequence defaults to
# the one of the published channel.json of --repo (default pithyx/pithyx) plus one. --work keeps the files here
# (default ~/pithyx-release/<version>). --publish needs gh signed in with write access to the releases; the release
# of a stable file (v<version>) needs its tag on GitHub. --yes skips the question before anything is uploaded.
set -euo pipefail
export LC_ALL=C.UTF-8

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
from=""
version=""
key=""
key_id=""
sequence=""
github=pithyx/pithyx
work=""
publish=false
yes=false

die() {
  echo "sign-remote: $*" >&2
  exit 1
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --from) from="${2%/}"; shift 2 ;;
    --version) version="$2"; shift 2 ;;
    --key) key="$2"; shift 2 ;;
    --key-id) key_id="$2"; shift 2 ;;
    --sequence) sequence="$2"; shift 2 ;;
    --repo) github="$2"; shift 2 ;;
    --work) work="$(realpath -m "$2")"; shift 2 ;;
    --publish) publish=true; shift ;;
    --yes) yes=true; shift ;;
    -h | --help) sed -n '2,14p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) die "unknown argument $1" ;;
  esac
done
[[ -n "$from" && -n "$version" && -n "$key" && -n "$key_id" ]] || die "--from, --version, --key and --key-id are required"
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]] || die "\"$version\" is not a version like 1.1.0"
[[ -z "$sequence" || "$sequence" =~ ^[1-9][0-9]*$ ]] || die "--sequence is a positive number"
[[ -r "$key" ]] || die "cannot read the key $key"
key="$(realpath "$key")"
if [[ "$publish" == true ]]; then
  command -v gh >/dev/null || die "--publish needs gh"
fi

# A folder that exists here is local; anything else with a colon is a host of scp.
remote=false
if [[ ! -d "$from" && "$from" == *:* ]]; then
  remote=true
  command -v scp >/dev/null || die "scp not found"
else
  [[ -d "$from" ]] || die "$from is neither a folder here nor <host>:<folder>"
  from="$(realpath "$from")"
fi
fetch() {
  if [[ "$remote" == true ]]; then scp -q "$@"; else cp "$@"; fi
}
put() {
  if [[ "$remote" == true ]]; then
    ssh "${from%%:*}" mkdir -p "${from#*:}/signed"
    scp -q "$@" "$from/signed/"
  else
    mkdir -p "$from/signed"
    cp "$@" "$from/signed/"
  fi
}

name="pithyx-$version-amd64.pithyxupdate"
work="${work:-$HOME/pithyx-release/$version}"
mkdir -p "$work"
cd "$work"
rm -rf channel
echo "fetching $name from $from"
fetch "$from/$name" "$from/$name.sha256" .
sha256sum --check --quiet "$name.sha256" || die "$name does not match its checksum; fetch it again"

"$repo/tools/release/sign-update.sh" "$name" --key "$key" --key-id "$key_id"
manifest="$(tar --extract --file="$name" --to-stdout manifest.json)"
channel="$(sed -n 's/^  "channel": "\(.*\)",$/\1/p' <<<"$manifest")"
commit="$(sed -n 's/^  "commit": "\(.*\)",$/\1/p' <<<"$manifest")"

if [[ -z "$sequence" ]]; then
  # Read without gh, so the default works on a machine that only signs; a channel that does not exist yet starts at 1.
  url="https://github.com/$github/releases/download/channel-$channel/channel.json"
  status="$(curl --silent --location --output current.json --write-out '%{http_code}' "$url" || true)"
  case "$status" in
    200) current="$(sed -n 's/^  "sequence": \([0-9]*\),$/\1/p' current.json)" ;;
    404) current=0 ;;
    *) die "cannot read $url (HTTP $status); pass --sequence" ;;
  esac
  rm -f current.json
  [[ -n "$current" ]] || die "the published channel.json has no sequence; pass --sequence"
  sequence=$((current + 1))
fi
"$repo/tools/release/build-channel.sh" "$name" --key "$key" --key-id "$key_id" --sequence "$sequence" --out channel

put "$name" "$name.sha256" channel/channel.json channel/channel.json.sig
echo "signed $name for $channel (channel.json sequence $sequence); copies in $work and $from/signed"
[[ "$publish" == true ]] || exit 0

if [[ "$channel" == stable ]]; then tag="v$version"; else tag="beta-$version"; fi
echo "publishing $name on $tag and channel.json (sequence $sequence) on channel-$channel of $github"
if [[ "$yes" != true ]]; then
  read -rp "continue? [y/N] " answer
  [[ "$answer" == [yY] ]] || die "nothing published; the signed files stay in $work"
fi
if ! gh release view "$tag" --repo "$github" >/dev/null 2>&1; then
  node -e 'process.stdout.write(JSON.parse(process.argv[1]).changelog || "")' "$manifest" >notes.md
  if [[ "$channel" == stable ]]; then
    gh release create "$tag" --repo "$github" --prerelease --verify-tag --title "Pithyx $version" --notes-file notes.md
  else
    gh release create "$tag" --repo "$github" --prerelease --target "$commit" --title "Pithyx $version (beta)" \
      --notes-file notes.md
  fi
fi
gh release upload "$tag" --repo "$github" --clobber "$name" "$name.sha256"
if ! gh release view "channel-$channel" --repo "$github" >/dev/null 2>&1; then
  gh release create "channel-$channel" --repo "$github" --prerelease --target "$commit" \
    --title "Pithyx $channel channel" \
    --notes "channel.json of the $channel channel: the newest $channel update file. Boxes on the $channel channel read it once a day."
fi
gh release upload "channel-$channel" --repo "$github" --clobber channel/channel.json channel/channel.json.sig
echo "published $tag; channel-$channel names $version with sequence $sequence"
