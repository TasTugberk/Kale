#!/usr/bin/env bash
# Publishes Kale's packages for applications (Kale.Auth.Contracts, then Kale.Auth.Client) to a NuGet feed, at the
# version in Directory.Build.props (<VersionPrefix>). Applications then only run `dotnet add package Kale.Auth.Client`.
#
#   scripts/publish-client.sh <feed>
#
#   <feed>  the feed's push address (e.g. your company's Nexus or BaGet), or a folder for a local feed
#
# A feed address needs its API key: give it on standard input (`... < key-file`), or type it when asked. It never
# goes on a command line, where other processes could see it.
set -euo pipefail

usage() {
  sed -n '5,8p' "$0" | sed 's/^# \{0,1\}//' >&2
  exit 2
}

fail() {
  echo "$1" >&2
  exit 1
}

[[ $# -eq 1 ]] || usage
FEED="$1"
command -v dotnet >/dev/null || fail "This script needs the .NET SDK (dotnet)."

cd "$(dirname "$0")/.."
VERSION="$(sed -n 's:.*<VersionPrefix>\(.*\)</VersionPrefix>.*:\1:p' Directory.Build.props)"
[[ -n "$VERSION" ]] || fail "No <VersionPrefix> in Directory.Build.props."

API_KEY=""
if [[ "$FEED" == http://* || "$FEED" == https://* ]]; then
  if [[ -t 0 ]]; then
    read -r -s -p "API key for $FEED: " API_KEY
    echo
  else
    read -r API_KEY || true
  fi
  [[ -n "$API_KEY" ]] || fail "Give the feed's API key on standard input."
else
  # A folder feed would silently overwrite a version, and applications that already have it cached would keep
  # the old copy: refuse here, as a server feed does (409).
  mkdir -p "$FEED"
  for package in Kale.Auth.Contracts Kale.Auth.Client; do
    [[ ! -e "$FEED/$package.$VERSION.nupkg" ]] \
      || fail "$package $VERSION is already published: raise <VersionPrefix> in Directory.Build.props, then publish again."
  done
fi

PACKAGES="$(mktemp -d)"
trap 'rm -rf "$PACKAGES"' EXIT
for project in Auth.Contracts Auth.Client; do
  dotnet pack "src/$project" -c Release -o "$PACKAGES" >/dev/null || fail "Couldn't pack src/$project."
done

# Contracts first: the client depends on it. NUGET_API_KEY reaches only this one dotnet process.
for package in Kale.Auth.Contracts Kale.Auth.Client; do
  if ! output="$(NUGET_API_KEY="$API_KEY" dotnet nuget push "$PACKAGES/$package.$VERSION.nupkg" --source "$FEED" 2>&1)"; then
    if grep -q -i -E "already exists|409|conflict" <<<"$output"; then
      fail "$package $VERSION is already published: raise <VersionPrefix> in Directory.Build.props, then publish again."
    fi
    echo "$output" >&2
    fail "Couldn't publish $package $VERSION."
  fi
  echo "Published $package $VERSION"
done

cat <<EOF

Applications can now add it:
    dotnet nuget add source $FEED --name kale --configfile nuget.config   (once per project or solution)
    dotnet add package Kale.Auth.Client --version $VERSION
EOF
