#!/usr/bin/env bash
#
# Da Vinci external interoperability harness — one command.
#
#   ./scripts/interop/run.sh                    # br-payer smoke (the default)
#   ./scripts/interop/run.sh br-payer smoke     # the same, spelled out
#   ./scripts/interop/run.sh unit               # harness unit tests only, no Docker
#
# The smoke path starts the pinned HL7 Da Vinci burden-reduction payer reference
# implementation, waits for it to actually serve FHIR, submits a synthetic prior
# authorization to it, validates the response, writes sanitized evidence to
# artifacts/interop/, and tears the stack down — including after a failure.
#
# Everything the external implementation sees is synthetic. No repository secret,
# cloud credential or Docker socket is exposed to it.
#
# Prerequisites: .NET 8 SDK, Docker (with the daemon running) and outbound HTTPS
# to Docker Hub and packages2.fhir.org — the reference implementation downloads
# its Da Vinci IG packages at startup and will not boot without that egress.
#
# On a host whose egress is proxied, copy interop/docker-compose.proxy.example.yml
# and export CHO_INTEROP_COMPOSE_OVERRIDE to point at your copy.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
COMPOSE_FILE="$REPO_ROOT/interop/docker-compose.interop.yml"
PROJECT_NAME="cho-interop"
TEST_PROJECT="$REPO_ROOT/tests/DaVinciInterop.Tests/DaVinciInterop.Tests.csproj"
ARTIFACTS="${CHO_INTEROP_ARTIFACTS:-$REPO_ROOT/artifacts/interop}"

TARGET="${1:-br-payer}"
MODE="${2:-smoke}"

if [[ "$TARGET" == "unit" || "$TARGET" == "--unit" ]]; then
  MODE="unit"
fi

# ── Unconditional cleanup ────────────────────────────────────────────────────
# The harness tears its own stack down, but a killed test run (Ctrl-C, a CI
# timeout) can leave containers behind. This trap is the backstop: nothing the
# harness starts outlives the script.
cleanup() {
  local status=$?
  if [[ "$MODE" != "unit" && "${CHO_INTEROP_KEEP_STACK:-0}" != "1" ]]; then
    echo "==> Tearing down the interop stack"
    docker compose --project-name "$PROJECT_NAME" --file "$COMPOSE_FILE" \
      ${CHO_INTEROP_COMPOSE_OVERRIDE:+--file "$CHO_INTEROP_COMPOSE_OVERRIDE"} \
      --profile interop-br --profile interop-br-provider --profile interop-cho \
      --profile interop-pdex --profile interop-dtr-inferno \
      down --volumes --remove-orphans --timeout 30 >/dev/null 2>&1 || true
  fi
  exit "$status"
}
trap cleanup EXIT INT TERM

if [[ "$MODE" == "unit" ]]; then
  echo "==> Harness unit tests (no Docker, no external code)"
  exec dotnet test "$TEST_PROJECT" -c Debug --filter "Category=DaVinciInteropUnit"
fi

if [[ "$TARGET" != "br-payer" ]]; then
  echo "Unknown interop target '$TARGET'." >&2
  echo "Executable targets in this repository: br-payer (scenario BR-PAS-SUBMIT-001)." >&2
  echo "Other targets are pinned in interop/versions.json but have no scenario yet;" >&2
  echo "see docs/interop/davinci.md for how to add one." >&2
  exit 2
fi

if ! docker info >/dev/null 2>&1; then
  echo "Docker is not running. The interop harness starts pinned third-party containers." >&2
  exit 2
fi

echo "==> Cleaning previous evidence from $ARTIFACTS"
rm -rf "$ARTIFACTS"

echo "==> Running the Da Vinci interoperability smoke scenario (BR-PAS-SUBMIT-001)"
echo "    target: HL7-DaVinci/br-payer, pinned by digest in interop/versions.json"

# The harness owns startup, readiness and teardown of the external stack; this
# script only enables it and collects what it produced.
CHO_INTEROP_ENABLED=1 \
CHO_INTEROP_ARTIFACTS="$ARTIFACTS" \
  dotnet test "$TEST_PROJECT" -c Debug --filter "Category=DaVinciInterop"

echo
echo "==> Evidence written to $ARTIFACTS"
if command -v python3 >/dev/null 2>&1 && [[ -f "$ARTIFACTS/run.json" ]]; then
  python3 - "$ARTIFACTS/run.json" <<'PY'
import json, sys
run = json.load(open(sys.argv[1]))
s = run["summary"]
print(f"    {s['passed']} passed / {s['failed']} failed / {s['skipped']} skipped / {s['notRun']} not run")
for target in run["targets"]:
    for result in target["results"]:
        print(f"    {result['scenarioId']}: {result['status']}  ({target['name']} @ {target['version']})")
for finding in run["findings"]:
    print(f"    [{finding['severity']}] {finding['code']}: {finding['summary']}")
PY
fi
