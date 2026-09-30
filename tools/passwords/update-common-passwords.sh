#!/usr/bin/env bash
# Regenerates the lists that new passwords are checked against.
# Source: the NCSC list of the 100,000 most used passwords from Have I Been Pwned, distributed by SecLists, ordered
# by frequency. Hashcat-style $hex[...] entries are skipped. Three files are written:
#   daemon/.../common-passwords.txt   every entry at least as long as the minimum length (the hard check)
#   desktop/.../common-passwords.txt  the most used of those, for the live check while typing
#   desktop/.../common-words.txt      the most used letters-only entries in frequency order, the strength estimate's
#                                     dictionary (a word's line number is its rank)
#
#   tools/passwords/update-common-passwords.sh [min-length]
set -euo pipefail
export LC_ALL=C.UTF-8

min_length=${1:-8}
client_passwords=5000
client_words=3000
source_url=https://raw.githubusercontent.com/danielmiessler/SecLists/master/Passwords/Common-Credentials/100k-most-used-passwords-NCSC.txt
root="$(git rev-parse --show-toplevel)"
daemon_target="$root/daemon/src/Pithyx.Daemon/Auth/common-passwords.txt"
desktop_dir="$root/desktop/src/auth/password"

source_file=$(mktemp)
trap 'rm -f "$source_file"' EXIT
curl -fsSL "$source_url" | tr -d '\r' | awk 'index(tolower($0), "$hex[") != 1 { print tolower($0) }' >"$source_file"

awk -v min="$min_length" 'length($0) >= min' "$source_file" | sort -u >"$daemon_target"
# mawk counts bytes, so the daemon list also keeps a few shorter multi-byte entries (harmless: they fail the length
# check first); the desktop lists keep ASCII only, where bytes and characters agree.
awk -v min="$min_length" -v max="$client_passwords" '/^[ -~]+$/ && length($0) >= min && !seen[$0]++ && n++ < max' "$source_file" |
  sort >"$desktop_dir/common-passwords.txt"
awk -v max="$client_words" '/^[a-z]+$/ && length($0) >= 4 && !seen[$0]++ && n++ < max' "$source_file" >"$desktop_dir/common-words.txt"

echo "wrote $(wc -l <"$daemon_target") passwords with at least $min_length characters to $daemon_target"
echo "wrote $client_passwords passwords and $client_words words to $desktop_dir"
