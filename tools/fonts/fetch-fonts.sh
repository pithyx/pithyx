#!/usr/bin/env bash
# Downloads the official font releases in pinned versions, verifies them and copies the files the desktop uses,
# unmodified, together with their licenses into desktop/public/fonts/. The copied files are committed; run this
# script only to update a font or to check where a file came from.
set -euo pipefail

repo_root=$(git -C "$(dirname "$0")" rev-parse --show-toplevel)
target="$repo_root/desktop/public/fonts"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

fetch() {
  local url=$1 file=$2 sha256=$3
  curl --fail --silent --show-error --location --output "$work/$file" "$url"
  echo "$sha256  $work/$file" | sha256sum --check --quiet
}

# IBM Plex Sans 1.1.0: IBM's own npm package, built from github.com/IBM/plex. Reserved Font Name "Plex":
# never subset, convert or rename these files.
fetch https://registry.npmjs.org/@ibm/plex-sans/-/plex-sans-1.1.0.tgz plex-sans.tgz \
  c3818979c2a2c82927ea3e7c485e389a18de0b7462887dd4f7acc9a525568962
tar -xzf "$work/plex-sans.tgz" -C "$work"
mkdir -p "$target/ibm-plex-sans"
for weight in Regular Italic Medium SemiBold; do
  install -m 0644 "$work/package/fonts/complete/woff2/IBMPlexSans-$weight.woff2" "$target/ibm-plex-sans/"
done
install -m 0644 "$work/package/LICENSE.txt" "$target/ibm-plex-sans/LICENSE.txt"

# Sora: upstream github.com/sora-xor/sora-font publishes WOFF2 itself, pinned to a commit.
sora=https://raw.githubusercontent.com/sora-xor/sora-font/7f9a9c5d0ccd1c099cfac420aa27133df1c5fdc4
fetch "$sora/fonts/woff2/Sora-SemiBold.woff2" Sora-SemiBold.woff2 \
  c8ef09866c1ce0e389507b89c9123b02f9766b17ab6abc6707cb2cecabf953dd
fetch "$sora/fonts/woff2/Sora-Bold.woff2" Sora-Bold.woff2 \
  cd24a779a2c983ee792c1a8c7e37f947aeff9cba10294fc84e90ff33242954c0
fetch "$sora/OFL.txt" sora-OFL.txt \
  ba0b9729c9428ba79a0459ab8ec575791b51509dbec213e383d0316d37fec299
mkdir -p "$target/sora"
install -m 0644 "$work/Sora-SemiBold.woff2" "$work/Sora-Bold.woff2" "$target/sora/"
install -m 0644 "$work/sora-OFL.txt" "$target/sora/OFL.txt"

# JetBrains Mono 2.304: the official release archive ships web fonts.
fetch https://github.com/JetBrains/JetBrainsMono/releases/download/v2.304/JetBrainsMono-2.304.zip jetbrains-mono.zip \
  6f6376c6ed2960ea8a963cd7387ec9d76e3f629125bc33d1fdcd7eb7012f7bbf
python3 -m zipfile -e "$work/jetbrains-mono.zip" "$work/jetbrains-mono"
mkdir -p "$target/jetbrains-mono"
for weight in Regular SemiBold; do
  install -m 0644 "$work/jetbrains-mono/fonts/webfonts/JetBrainsMono-$weight.woff2" "$target/jetbrains-mono/"
done
install -m 0644 "$work/jetbrains-mono/OFL.txt" "$target/jetbrains-mono/OFL.txt"

echo "Fonts copied to $target"
