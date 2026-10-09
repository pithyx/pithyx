#!/usr/bin/env bash
# Publishes a signed beta update file (D338) on the private repository: a GitHub pre-release beta-<version> at the
# file's commit with the file, then a new channel.json (the sequence of the current one plus one) on the moving
# pre-release channel-beta, which every box on the beta channel reads once a day.
#
#   tools/release/publish-beta.sh <file.pithyxupdate> [--key <pem file>] [--key-id <id>] [--repo <owner/name>] [--yes]
#
# Beta only: stable files, their tags v<version> on master and the release channel-stable are the owner's. The key
# defaults to pithyx-beta-1 in ~/.config/pithyx-dev/. Needs gh signed in with write access to the releases; --yes
# skips the question before anything is uploaded.
set -euo pipefail
export LC_ALL=C.UTF-8

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
file=""
key="$HOME/.config/pithyx-dev/pithyx-beta-1.pem"
key_id=pithyx-beta-1
github=pithyx/pithyx
yes=false

die() {
  echo "publish-beta: $*" >&2
  exit 1
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --key) key="$2"; shift 2 ;;
    --key-id) key_id="$2"; shift 2 ;;
    --repo) github="$2"; shift 2 ;;
    --yes) yes=true; shift ;;
    -h | --help) sed -n '2,10p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
    -*) die "unknown argument $1" ;;
    *) file="$(realpath "$1")"; shift ;;
  esac
done
[[ -f "$file" && "$file" == *.pithyxupdate ]] || die "name a signed .pithyxupdate file"
command -v gh >/dev/null || die "gh not found"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
tar --extract --file="$file" --directory="$work" manifest.json manifest.json.sig 2>/dev/null || die "$file is not signed"
channel="$(sed -n 's/^  "channel": "\(.*\)",$/\1/p' "$work/manifest.json")"
version="$(sed -n 's/^  "version": "\(.*\)",$/\1/p' "$work/manifest.json")"
commit="$(sed -n 's/^  "commit": "\(.*\)",$/\1/p' "$work/manifest.json")"
[[ "$channel" == beta ]] || die "$file is a $channel file; only beta files are published here"
node "$repo/tools/release/update-file.ts" verify-manifest "$work/manifest.json" --channel beta || die "$file does not verify"
target="$(git -C "$repo" rev-parse "$commit")"
git -C "$repo" branch --remotes --contains "$target" | grep -q . || die "commit $commit is not pushed"

sequence=0
if gh release download channel-beta --repo "$github" --pattern channel.json --dir "$work/current" >/dev/null 2>&1; then
  sequence="$(sed -n 's/^  "sequence": \([0-9]*\),$/\1/p' "$work/current/channel.json")"
fi
next=$((sequence + 1))
tag="beta-$version"
echo "publishing $(basename "$file") as $tag at $commit, channel-beta sequence $sequence -> $next on $github"
if [[ "$yes" != true ]]; then
  read -rp "continue? [y/N] " answer
  [[ "$answer" == [yY] ]] || die "nothing published"
fi

node -e 'const m = JSON.parse(require("fs").readFileSync(process.argv[1], "utf8")); process.stdout.write(m.changelog || "")' \
  "$work/manifest.json" >"$work/notes.md"
gh release create "$tag" --repo "$github" --prerelease --target "$target" --title "Pithyx $version (beta)" \
  --notes-file "$work/notes.md" "$file"
"$repo/tools/release/build-channel.sh" "$file" --key "$key" --key-id "$key_id" --sequence "$next" --out "$work/channel"
if ! gh release view channel-beta --repo "$github" >/dev/null 2>&1; then
  gh release create channel-beta --repo "$github" --prerelease --target "$target" --title "Pithyx beta channel" \
    --notes "channel.json of the beta channel: the newest beta update file. Boxes on the beta channel read it once a day."
fi
gh release upload channel-beta --repo "$github" --clobber "$work/channel/channel.json" "$work/channel/channel.json.sig"
echo "published $tag; channel-beta names $version with sequence $next"
