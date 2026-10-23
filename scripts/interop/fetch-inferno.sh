#!/usr/bin/env bash
#
# Fetches an Inferno conformance test kit at its pinned tag.
#
#   ./scripts/interop/fetch-inferno.sh pdex
#   ./scripts/interop/fetch-inferno.sh dtr
#   ./scripts/interop/fetch-inferno.sh c4bb
#   ./scripts/interop/fetch-inferno.sh us-core
#   ./scripts/interop/fetch-inferno.sh us-core c4bb   # several at once
#
# Upstream publishes no container image for these kits, so the harness builds one
# from a checkout. This is an EXPLICIT setup step on purpose: no ordinary test run
# may download third-party code. The checkout lands in interop/.external/, which is
# git-ignored — upstream sources are never committed into this repository.
#
# INFERNO-USCORE-SERVER-001 and INFERNO-C4BB-SERVER-001 run against checkouts
# this script produces; ./scripts/interop/run.sh inferno refuses to start without
# them. See docs/interop/davinci.md.

set -euo pipefail

# Several kits in one call: re-invoke once per kit so each still gets the full
# pin check, rather than a loop that could skip one on a partial failure.
if [[ $# -gt 1 ]]; then
  for kit in "$@"; do
    "${BASH_SOURCE[0]}" "$kit"
  done
  exit 0
fi

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
EXTERNAL_DIR="$REPO_ROOT/interop/.external"
VERSIONS="$REPO_ROOT/interop/versions.json"

KIT="${1:-}"
case "$KIT" in
  pdex)    KEY="inferno-pdex";    DIR_NAME="davinci-pdex-test-kit" ;;
  dtr)     KEY="inferno-dtr";     DIR_NAME="davinci-dtr-test-kit" ;;
  c4bb)    KEY="inferno-c4bb";    DIR_NAME="carin-for-blue-button-test-kit" ;;
  us-core) KEY="inferno-us-core"; DIR_NAME="us-core-test-kit" ;;
  *)       echo "Usage: $0 {pdex|dtr|c4bb|us-core}" >&2; exit 2 ;;
esac

read -r REPO TAG COMMIT < <(python3 - "$VERSIONS" "$KEY" <<'PY'
import json, sys
versions = json.load(open(sys.argv[1]))
target = next(t for t in versions["targets"] if t["key"] == sys.argv[2])
print(target["upstreamRepository"], target["pin"]["tag"], target["pin"]["commit"])
PY
)

DEST="$EXTERNAL_DIR/$DIR_NAME"
mkdir -p "$EXTERNAL_DIR"

if [[ -d "$DEST/.git" ]]; then
  echo "==> $DIR_NAME already present; fetching the pinned tag"
  git -C "$DEST" fetch --depth 1 origin "refs/tags/$TAG:refs/tags/$TAG" --force
else
  echo "==> Cloning $REPO at $TAG"
  git clone --depth 1 --branch "$TAG" "$REPO" "$DEST"
fi

git -C "$DEST" checkout --quiet "$TAG"

ACTUAL="$(git -C "$DEST" rev-parse HEAD)"
if [[ "$ACTUAL" != "$COMMIT" ]]; then
  echo "Pin mismatch: interop/versions.json records $COMMIT for $KEY, but $TAG resolves to $ACTUAL." >&2
  echo "An upstream tag was moved. Do not silently accept it — record the change deliberately." >&2
  exit 1
fi

echo "==> $DIR_NAME checked out at $TAG ($ACTUAL)"
echo "    Upstream license: see $DEST/LICENSE (Apache-2.0). Not vendored into this repository."
