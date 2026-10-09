#!/usr/bin/env bash
# Packs an unpacked update file (manifest.json, an optional manifest.json.sig and debs/) into a .pithyxupdate the same
# way every time: manifest.json first so the box reads it before the packages, then its signature, then debs/ sorted by
# name, root as owner and every time the manifest's publishedAt. Used by build-update.sh and sign-update.sh.
#
#   tools/release/pack-update.sh <folder> <file>
set -euo pipefail
export LC_ALL=C.UTF-8

[[ "${1:-}" == -h || "${1:-}" == --help ]] && { sed -n '2,6p' "$0" | sed 's/^# \{0,1\}//'; exit 0; }
[[ $# -eq 2 ]] || { echo "pack-update: usage: pack-update.sh <folder> <file>" >&2; exit 1; }
folder="$1"
file="$(realpath -m "$2")"
published="$(sed -n 's/^  "publishedAt": "\(.*\)",$/\1/p' "$folder/manifest.json")"
[[ -n "$published" ]] || { echo "pack-update: no publishedAt in $folder/manifest.json" >&2; exit 1; }

members=(manifest.json)
[[ -f "$folder/manifest.json.sig" ]] && members+=(manifest.json.sig)
members+=(debs)
rm -f "$file"
tar --create --file="$file" --directory="$folder" --format=gnu --sort=name --mtime="$published" \
  --owner=0 --group=0 --numeric-owner --mode='u=rwX,go=rX' "${members[@]}"
