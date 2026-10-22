#!/usr/bin/env bash
#
# Builds and runs the MS-DRG parity oracle.
#
# The CMS jars are not in this repository and cannot be: CMS distributes them
# per-requester and they carry AHA UB-04 copyright. Point CMS_MSDRG_JARS at a
# directory holding the four jars listed below and this script does the rest.
#
#   CMS_MSDRG_JARS=/path/to/jars ./run.sh [corpus] [output]
#
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
corpus="${1:-$here/corpus/reference-claims.psv}"
output="${2:-$here/corpus/expected-v44.psv}"
build="$here/build"

if [[ -z "${CMS_MSDRG_JARS:-}" ]]; then
  echo "CMS_MSDRG_JARS is not set. See the README for how to obtain the jars." >&2
  exit 1
fi

required=(
  "msdrg-core-44.0.0.3.jar"
  "msdrg-model-v2-2.12.2.jar"
  "msdrg-binary-access-1.6.4.jar"
)
for jar in "${required[@]}"; do
  if [[ ! -f "$CMS_MSDRG_JARS/$jar" ]]; then
    echo "missing $CMS_MSDRG_JARS/$jar" >&2
    exit 1
  fi
done

# protobuf-java reads the grouper's table buffer; slf4j-api satisfies the jars'
# logging calls. Both are ordinary open-source artifacts, resolved by the
# caller or by CI into the same directory.
for jar in protobuf-java.jar slf4j-api.jar; do
  if [[ ! -f "$CMS_MSDRG_JARS/$jar" ]]; then
    echo "missing $CMS_MSDRG_JARS/$jar — see the README" >&2
    exit 1
  fi
done

rm -rf "$build"
mkdir -p "$build/shim" "$build/oracle"

echo "Compiling the foundation shim..."
javac -d "$build/shim" $(find "$here/foundation-shim/src" -name '*.java')

classpath="$build/shim"
for jar in "$CMS_MSDRG_JARS"/*.jar; do
  classpath="$classpath:$jar"
done

echo "Compiling the oracle..."
javac -cp "$classpath" -d "$build/oracle" \
  $(find "$here/src/main/java" -name '*.java')

echo "Running the oracle..."
java -Xmx3g -cp "$classpath:$build/oracle" \
  gov.cho.grouper.oracle.ParityOracle "$corpus" "$output"
