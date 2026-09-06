#!/usr/bin/env bash
#
# Da Vinci external interoperability harness — one command.
#
#   ./scripts/interop/run.sh                    # br-payer smoke (the default)
#   ./scripts/interop/run.sh br-payer smoke       # PAS $submit  (BR-PAS-SUBMIT-001)
#   ./scripts/interop/run.sh br-payer crd         # CRD CDS Hooks (BR-CRD-001)
#   ./scripts/interop/run.sh br-payer dtr         # DTR $questionnaire-package (BR-DTR-001)
#   ./scripts/interop/run.sh br-payer pas-inquire # PAS $submit -> $inquire (BR-PAS-INQUIRE-001)
#   ./scripts/interop/run.sh br-payer all         # all four, into one evidence document
#   ./scripts/interop/run.sh unit               # harness unit tests only, no Docker
#
# Each scenario starts the pinned HL7 Da Vinci burden-reduction payer reference
# implementation, waits for it to actually be ready, performs a real exchange
# against it, validates the response, writes sanitized evidence to
# artifacts/interop/, and tears the stack down — including after a failure.
#
# The smoke path submits a synthetic prior authorization (PAS $submit). The CRD
# path discovers the payer's CDS Hooks services and invokes its order-sign CRD
# service with synthetic draft orders. The DTR path enters from the payer's own
# CRD determination and follows the questionnaire canonical it named into
# $questionnaire-package. The pas-inquire path performs its own $submit, takes the
# authorization identity the payer issued for it, and inquires on exactly that.
# Scenarios run one at a time: they share a Compose project and host ports, so the
# suite serializes them deliberately.
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

# Each mode maps to the scenario ids it runs, via the [Trait("Scenario", …)] the
# scenario tests carry. `all` runs every external scenario in one invocation; the
# evidence writer merges their results into a single run document.
case "$MODE" in
  smoke)       FILTER='Scenario=BR-PAS-SUBMIT-001';  LABEL='PAS $submit smoke (BR-PAS-SUBMIT-001)' ;;
  crd)         FILTER='Scenario=BR-CRD-001';         LABEL='CRD CDS Hooks (BR-CRD-001)' ;;
  dtr)         FILTER='Scenario=BR-DTR-001';         LABEL='DTR $questionnaire-package, chained from CRD (BR-DTR-001)' ;;
  pas-inquire) FILTER='Scenario=BR-PAS-INQUIRE-001'; LABEL='PAS $submit then $inquire (BR-PAS-INQUIRE-001)' ;;
  all)         FILTER='Category=DaVinciInterop';     LABEL='all external scenarios' ;;
  unit)  : ;;
  *)
    echo "Unknown mode '$MODE'. Modes: smoke | crd | dtr | pas-inquire | all | unit." >&2
    exit 2
    ;;
esac

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
  echo "Executable targets in this repository: br-payer (BR-PAS-SUBMIT-001, BR-CRD-001," >&2
  echo "BR-DTR-001, BR-PAS-INQUIRE-001)." >&2
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

echo "==> Running Da Vinci interoperability: $LABEL"
echo "    target: HL7-DaVinci/br-payer, pinned by digest in interop/versions.json"

# The harness owns startup, readiness and teardown of the external stack; this
# script only enables it and collects what it produced.
CHO_INTEROP_ENABLED=1 \
CHO_INTEROP_ARTIFACTS="$ARTIFACTS" \
  dotnet test "$TEST_PROJECT" -c Debug --filter "$FILTER"

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
        # Both chain fields are optional in the evidence schema, so neither is
        # indexed directly — a reporting path must not be the thing that fails.
        linked_from = result.get("linkedFromScenario")
        linked_artifact = result.get("linkedArtifact")
        chain = f"  ← {linked_from}" if linked_from else ""
        print(f"    {result['scenarioId']}: {result['status']}{chain}  ({target['name']} @ {target['version']})")
        if linked_artifact:
            source = f" from {linked_from}" if linked_from else ""
            print(f"        consumed{source}: {linked_artifact}")
for finding in run["findings"]:
    print(f"    [{finding['severity']}] {finding['code']}: {finding['summary']}")
PY
fi
