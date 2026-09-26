#!/usr/bin/env bash
# Regenerates the embedded list of common passwords that new passwords are checked against.
# Source: the NCSC list of the 100,000 most used passwords from Have I Been Pwned, distributed by SecLists.
# Only entries at least as long as the minimum password length can ever match, so shorter ones are dropped,
# and hashcat-style $hex[...] entries are skipped.
#
#   tools/passwords/update-common-passwords.sh [min-length]
set -euo pipefail
export LC_ALL=C.UTF-8

min_length=${1:-12}
source_url=https://raw.githubusercontent.com/danielmiessler/SecLists/master/Passwords/Common-Credentials/100k-most-used-passwords-NCSC.txt
target="$(git rev-parse --show-toplevel)/daemon/src/Vyxis.Daemon/Auth/common-passwords.txt"

curl -fsSL "$source_url" |
  tr -d '\r' |
  awk -v min="$min_length" 'index(tolower($0), "$hex[") != 1 && length($0) >= min { print tolower($0) }' |
  sort -u >"$target"

echo "wrote $(wc -l <"$target") passwords with at least $min_length characters to $target"
