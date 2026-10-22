#!/usr/bin/env bash
#
# Starts CMS's IPPS pricer, asks it what each reference stay pays, and stops it.
#
# The CMS jar is not in this repository and cannot be. CMS publishes it to
# satisfy Freedom of Information Act requests, with no licence attached and no
# support, so this repository holds only the questions and the answers. Point
# CMS_IPPS_PRICER_JAR at the jar and this script does the rest.
#
#   CMS_IPPS_PRICER_JAR=/path/to/ipps-pricer-application-2.14.1.jar ./run.sh
#
# The pricer is a local HTTP service. Nothing leaves the machine.
#
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
corpus="${1:-$here/corpus/reference-claims.json}"
output="${2:-$here/corpus/expected-fy2026.psv}"
port="${CMS_IPPS_PRICER_PORT:-8089}"

if [[ -z "${CMS_IPPS_PRICER_JAR:-}" ]]; then
  echo "CMS_IPPS_PRICER_JAR is not set. See the README for how to obtain it." >&2
  exit 1
fi
if [[ ! -f "$CMS_IPPS_PRICER_JAR" ]]; then
  echo "not a file: $CMS_IPPS_PRICER_JAR" >&2
  exit 1
fi

log="$(mktemp -t ipps-pricer-XXXXXX.log)"
pricer_pid=""

cleanup() {
  if [[ -n "$pricer_pid" ]] && kill -0 "$pricer_pid" 2>/dev/null; then
    kill "$pricer_pid" 2>/dev/null || true
    wait "$pricer_pid" 2>/dev/null || true
  fi
}
trap cleanup EXIT

# --add-opens is CMS's own instruction, not a workaround of ours: the pricer
# reflects over java.base and will not start without it.
echo "Starting the pricer on port $port..."
java --add-opens java.base/java.lang=ALL-UNNAMED \
  -Ddw.supportedYears=2024,2025,2026,2027 \
  -Ddw.server.applicationConnectors\[0\].port="$port" \
  -jar "$CMS_IPPS_PRICER_JAR" server >"$log" 2>&1 &
pricer_pid=$!

base_url="http://localhost:$port"
for _ in $(seq 1 60); do
  if curl -fsS --noproxy '*' "$base_url/v1/supported-years" >/dev/null 2>&1; then
    break
  fi
  if ! kill -0 "$pricer_pid" 2>/dev/null; then
    echo "the pricer exited before it was ready; its log follows" >&2
    cat "$log" >&2
    exit 1
  fi
  sleep 1
done

if ! curl -fsS --noproxy '*' "$base_url/v1/supported-years" >/dev/null 2>&1; then
  echo "the pricer did not become ready within 60s; its log follows" >&2
  cat "$log" >&2
  exit 1
fi

echo "Asking it about every reference stay..."
python3 -I "$here/oracle.py" "$base_url" "$corpus" "$output"
