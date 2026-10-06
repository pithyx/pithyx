#!/usr/bin/env bash
# Publishes the npm packages of Pithyx (@pithyx/sdk; later @pithyx/ui and @pithyx/cli) at their shared version (D306).
#
#   tools/release/publish-sdk.sh [--tag <dist-tag>] [--latest] [--otp <code>] [--yes] [--dry-run]
#
# The owner runs it by hand, signed in with `npm login`; no npm token is stored anywhere. It refuses a dirty tree and a
# HEAD that is not on origin/dev or origin/master, builds, type-checks and tests every package, packs it with pnpm
# (which replaces `workspace:` ranges by versions and applies `publishConfig`, so the published `exports` lose the
# monorepo's `pithyx-source` condition), shows what each tarball contains, asks, publishes the tarballs with npm, and
# only then creates and pushes the git tag `sdk-v<version>`.
#
# Dist-tags: a prerelease (1.0.0-rc.1) goes to `next` unless --tag names another; a stable version needs --latest and
# goes to `latest`. `latest` never takes a prerelease. npm asks for the one-time password of the 2FA unless --otp gives
# it. A package whose version is already on the registry is skipped, so a run that stopped half way can be repeated.
# --dry-run builds, packs and runs `npm publish --dry-run`, and only warns about the git checks.
set -euo pipefail

# Every package of the shared version line, relative to the repository root. @pithyx/ui and @pithyx/cli join here (A8).
readonly PACKAGES=(sdk)

die() {
  echo "publish-sdk: $*" >&2
  exit 1
}

usage() {
  sed -n '2,15p' "$0" | sed 's/^# \{0,1\}//'
  exit "${1:-0}"
}

tag=""
latest=false
otp=""
yes=false
dry_run=false
while [[ $# -gt 0 ]]; do
  case "$1" in
    --tag)
      [[ $# -ge 2 ]] || die "--tag needs a dist-tag"
      tag="$2"
      shift 2
      ;;
    --latest)
      latest=true
      shift
      ;;
    --otp)
      [[ $# -ge 2 ]] || die "--otp needs a code"
      otp="$2"
      shift 2
      ;;
    --yes)
      yes=true
      shift
      ;;
    --dry-run)
      dry_run=true
      shift
      ;;
    -h | --help) usage ;;
    *) usage 2 >&2 ;;
  esac
done

repo="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$repo"

for tool in git node pnpm npm tar; do
  command -v "$tool" >/dev/null || die "$tool is not installed"
done

# A check that only warns in a dry run, which publishes and tags nothing.
refuse() {
  if [[ "$dry_run" == true ]]; then
    echo "publish-sdk: warning (dry run): $*" >&2
  else
    die "$*"
  fi
}

package_field() {
  node -e 'const p = require(process.argv[1]); console.log(p[process.argv[2]] ?? "")' "$repo/$1/package.json" "$2"
}

version=""
names=()
for dir in "${PACKAGES[@]}"; do
  name="$(package_field "$dir" name)"
  own="$(package_field "$dir" version)"
  [[ -n "$name" && -n "$own" ]] || die "$dir/package.json has no name or version"
  if [[ -z "$version" ]]; then
    version="$own"
  elif [[ "$own" != "$version" ]]; then
    die "$name is at $own, but the packages share one version ($version)"
  fi
  names+=("$name")
done

semver='^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?$'
[[ "$version" =~ $semver ]] || die "version $version is not SemVer (build metadata is not used)"
prerelease=false
[[ "$version" == *-* ]] && prerelease=true

if [[ -z "$tag" ]]; then
  if [[ "$latest" == true ]]; then
    tag=latest
  elif [[ "$prerelease" == true ]]; then
    tag=next
  else
    die "$version is a stable version: pass --latest to publish it as latest (only after the owner's approval)"
  fi
fi
[[ "$tag" =~ ^[a-z][a-z0-9-]*$ ]] || die "dist-tag '$tag' is not a lowercase name"
[[ ! "$tag" =~ ^v?[0-9] ]] || die "a dist-tag must not look like a version"
if [[ "$tag" == latest ]]; then
  [[ "$prerelease" == false ]] || die "latest never takes a prerelease ($version); use next"
  [[ "$latest" == true ]] || die "publishing under latest needs --latest"
elif [[ "$latest" == true ]]; then
  die "--latest and --tag $tag contradict each other"
fi
git_tag="sdk-v$version"

if [[ -n "$(git status --porcelain)" ]]; then
  refuse "the working tree is not clean; commit or stash first"
fi
git fetch --quiet origin dev master
head="$(git rev-parse HEAD)"
if ! git merge-base --is-ancestor "$head" origin/dev && ! git merge-base --is-ancestor "$head" origin/master; then
  refuse "HEAD ($head) is not on origin/dev or origin/master; publish only pushed, merged commits"
fi
if git rev-parse --quiet --verify "refs/tags/$git_tag" >/dev/null ||
  [[ -n "$(git ls-remote --tags origin "refs/tags/$git_tag")" ]]; then
  die "the tag $git_tag exists already; raise the version first"
fi

if [[ "$dry_run" == false ]]; then
  user="$(npm whoami 2>/dev/null)" || die "not signed in to npm; run npm login first (the token stays with npm, never in the repository)"
  echo "Signed in to npm as $user."
fi

echo "Building and testing ${names[*]} at $version"
pnpm install --frozen-lockfile
for dir in "${PACKAGES[@]}"; do
  pnpm --dir "$dir" run build
  pnpm --dir "$dir" run typecheck
  pnpm --dir "$dir" run test
done

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
tarballs=()
for i in "${!PACKAGES[@]}"; do
  dir="${PACKAGES[$i]}"
  name="${names[$i]}"
  mkdir "$work/$i"
  pnpm --dir "$dir" pack --pack-destination "$work/$i" >/dev/null
  packed=("$work/$i"/*.tgz)
  tarball="${packed[0]}"
  [[ ${#packed[@]} -eq 1 && -f "$tarball" ]] || die "pnpm pack did not write one tarball for $name"
  manifest="$(tar -xzOf "$tarball" package/package.json)"
  if grep -q '"workspace:' <<<"$manifest"; then
    die "$name still has a workspace: range after packing"
  fi
  if grep -q 'pithyx-source' <<<"$manifest"; then
    die "$name still exports the pithyx-source condition after packing (publishConfig.exports is missing)"
  fi
  echo
  echo "== $name@$version, dist-tag $tag"
  npm publish "$tarball" --dry-run --access public --tag "$tag"
  tarballs+=("$tarball")
done

if [[ "$dry_run" == true ]]; then
  echo
  echo "Dry run: nothing was published and no tag was created."
  exit 0
fi

first=()
for name in "${names[@]}"; do
  if ! npm view "$name" name >/dev/null 2>&1; then
    first+=("$name")
  fi
done
if [[ ${#first[@]} -gt 0 && "$tag" != latest ]]; then
  echo
  echo "Note: ${first[*]} is not on npm yet. On a first publish the registry also points latest at $version,"
  echo "until a stable version is published with --latest."
fi

echo
if [[ "$yes" == false ]]; then
  read -r -p "Publish ${names[*]} $version under '$tag' and push the tag $git_tag? Type the version to confirm: " answer
  [[ "$answer" == "$version" ]] || die "not confirmed; nothing was published"
fi

otp_args=()
[[ -z "$otp" ]] || otp_args=(--otp "$otp")
for i in "${!tarballs[@]}"; do
  name="${names[$i]}"
  if [[ "$(npm view "$name@$version" version 2>/dev/null)" == "$version" ]]; then
    echo "$name@$version is on npm already; skipped."
    continue
  fi
  npm publish "${tarballs[$i]}" --access public --tag "$tag" "${otp_args[@]}" ||
    die "publishing $name failed; nothing was tagged. Fix the cause and run the script again."
  echo "Published $name@$version under $tag."
done

git tag -a "$git_tag" -m "${names[*]} $version" "$head"
git push origin "refs/tags/$git_tag"
echo "Tagged $head as $git_tag and pushed the tag."
npm view "${names[0]}" dist-tags
