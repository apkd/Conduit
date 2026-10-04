#!/usr/bin/env bash
set -euo pipefail

fail() { echo "::error::$*" >&2; exit 1; }
[[ $# == 4 ]] || fail 'Usage: archive-release.sh <owner/repo> <version> <asset-directory> <metadata-path>'
repository=$1 version=$2 directory=$3 metadata=$4
[[ $version =~ ^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$ ]] || fail 'Expected a stable MAJOR.MINOR.PATCH version.'

archive_tag=build-archive
api="repos/$repository"
scratch=$(mktemp -d)
trap 'rm -r -- "$scratch"' EXIT
platforms=(linux-x64 win-x64.exe)

file_digest() { sha256sum "$1" | cut -d ' ' -f 1; }

names=() digests=()
for platform in "${platforms[@]}"; do
  source_path="$directory/conduit-$platform"
  [[ -s $source_path ]] || fail "Missing or empty binary: $source_path"
  names+=("conduit-$version-$platform")
  digests+=("sha256:$(file_digest "$source_path")")
done
hash="sha256-$(openssl dgst -sha256 -binary "$directory/conduit-linux-x64" | base64 -w0)"

gh api "$api/releases/tags/$archive_tag" > "$scratch/archive.json"
jq -e '.draft == false and .prerelease == false and (.immutable != true)' "$scratch/archive.json" >/dev/null \
  || fail 'Build archive must be public, mutable, and labelled None.'
archive_id=$(jq -r '.id' "$scratch/archive.json")
[[ $(gh api "$api/releases/latest" | jq -r '.id') != "$archive_id" ]] || fail 'The build archive must not be the Latest release.'

list_assets() {
  gh api --paginate --slurp "$api/releases/$archive_id/assets?per_page=100" | jq 'add' > "$scratch/assets.json"
}

verify_asset() {
  local asset=$1 expected=$2 digest id
  digest=$(jq -r '.digest // empty' <<< "$asset")
  if [[ ! $digest =~ ^sha256:[a-f0-9]{64}$ ]]; then
    id=$(jq -r '.id' <<< "$asset")
    gh api -H 'Accept: application/octet-stream' "$api/releases/assets/$id" > "$scratch/download"
    digest="sha256:$(file_digest "$scratch/download")"
  fi
  [[ $digest == "$expected" ]] || fail "Archived bytes differ: $(jq -r '.name' <<< "$asset"). Nothing will be overwritten."
}

list_assets
missing=()
for index in "${!names[@]}"; do
  asset=$(jq -c --arg name "${names[$index]}" '.[] | select(.name == $name)' "$scratch/assets.json")
  if [[ -n $asset ]]; then
    verify_asset "$asset" "${digests[$index]}"
  else
    missing+=("$index")
  fi
done
(( $(jq 'length' "$scratch/assets.json") + ${#missing[@]} <= 1000 )) \
  || fail "The archive would exceed GitHub's 1,000-asset limit. Create a new archive before publishing."

# preflight every existing asset before attempting any uploads; never use --clobber.
for index in "${missing[@]}"; do
  cp "$directory/conduit-${platforms[$index]}" "$scratch/${names[$index]}"
  gh release upload "$archive_tag" "$scratch/${names[$index]}" --repo "$repository"
done
list_assets
for index in "${!names[@]}"; do
  asset=$(jq -ce --arg name "${names[$index]}" '.[] | select(.name == $name)' "$scratch/assets.json")
  verify_asset "$asset" "${digests[$index]}"
done

# only verified, publicly available binaries may enter the Nix package metadata.
mkdir -p "$(dirname "$metadata")"
jq -n --arg version "$version" \
  --arg url "https://github.com/$repository/releases/download/$archive_tag/${names[0]}" --arg hash "$hash" \
  '{version: $version, url: $url, hash: $hash}' > "$metadata.tmp"
mv "$metadata.tmp" "$metadata"
echo "Archived Conduit $version; wrote $metadata."
