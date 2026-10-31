#!/usr/bin/env bash
# compile-cql.sh — Compile the DTR CQL libraries in fhir-service's DtrContent
# folder to ELM JSON with the HL7 CQL-to-ELM translator.
#
# DTR requires every Library in a $questionnaire-package to carry both its CQL
# and its ELM (DTR 2.2.0 oper-13). CHO does not run CQL itself (decision Q-6):
# the provider's DTR client does. So the ELM is generated here, committed beside
# the CQL, and served as content.
#
# For each Name.cql this writes:
#   Name.elm.json     the translator's ELM output
#   Name.cql.sha256   the SHA-256 of the CQL it was compiled from
# DtrContentLibraryTests fails when a .cql no longer matches its .sha256, so a
# CQL edit without a recompile cannot merge.
#
# Needs Java 17+ and Maven (to fetch the translator from Maven Central).
#
# Usage:
#   ./scripts/dtr/compile-cql.sh            # compile every library
#   ./scripts/dtr/compile-cql.sh --check    # fail if any ELM is out of date

set -euo pipefail

TRANSLATOR_VERSION="3.29.0"
REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
CONTENT_DIR="$REPO_ROOT/src/services/fhir-service/DtrContent"
CHECK_ONLY=false
[[ "${1:-}" == "--check" ]] && CHECK_ONLY=true

command -v java >/dev/null || { echo "java is required" >&2; exit 1; }
command -v mvn >/dev/null || { echo "mvn is required" >&2; exit 1; }

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

cat > "$WORK/pom.xml" <<POM
<project xmlns="http://maven.apache.org/POM/4.0.0">
  <modelVersion>4.0.0</modelVersion>
  <groupId>com.cloudhealthoffice</groupId>
  <artifactId>dtr-cql-compile</artifactId>
  <version>1</version>
  <dependencies>
    <dependency><groupId>info.cqframework</groupId><artifactId>cql-to-elm-cli</artifactId><version>$TRANSLATOR_VERSION</version></dependency>
    <dependency><groupId>info.cqframework</groupId><artifactId>quick</artifactId><version>$TRANSLATOR_VERSION</version></dependency>
  </dependencies>
</project>
POM
mvn -q -B -f "$WORK/pom.xml" dependency:copy-dependencies -DoutputDirectory="$WORK/lib"

status=0
shopt -s nullglob
for cql in "$CONTENT_DIR"/*.cql; do
  name="$(basename "$cql" .cql)"
  out="$WORK/out/$name"
  mkdir -p "$out"
  log="$(java -cp "$WORK/lib/*" org.cqframework.cql.cql2elm.cli.Main \
    --input "$cql" --format JSON --output "$out" --result-types --detailed-errors 2>&1)"
  if ! grep -q "Translation completed successfully" <<<"$log"; then
    # Warnings count as failures: a terminology warning, for one, means an
    # engine may skip the value-set filter it was written to apply.
    echo "$log" >&2
    echo "FAIL $name: translation did not complete cleanly" >&2
    status=1
    continue
  fi
  hash="$(sha256sum "$cql" | cut -d' ' -f1)"
  if $CHECK_ONLY; then
    if [[ "$(cat "$CONTENT_DIR/$name.cql.sha256" 2>/dev/null)" != "$hash" ]] \
      || ! cmp -s "$out/$name.json" "$CONTENT_DIR/$name.elm.json"; then
      echo "STALE $name: run scripts/dtr/compile-cql.sh" >&2
      status=1
    else
      echo "ok $name"
    fi
  else
    cp "$out/$name.json" "$CONTENT_DIR/$name.elm.json"
    echo "$hash" > "$CONTENT_DIR/$name.cql.sha256"
    echo "compiled $name"
  fi
done
exit $status
